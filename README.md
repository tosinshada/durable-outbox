# DurableOutbox

A transactional outbox for .NET 10 services on PostgreSQL through EF Core and Npgsql.

A service writes a message in the same database transaction as its business change. Background workers then claim each message under a lease (a time-limited claim) and hand it to a handler the service wrote, retrying until it is done or parked for an operator.

It is an outbox and a worker. It is not a message bus and needs no broker. The library never calls anything outside the database: your handler does.

| Package | What it is |
| :--- | :--- |
| `DurableOutbox` | The writer, the workers, the EF Core configuration, the operator API, telemetry and the health check. |
| `DurableOutbox.Testing` | The guarantee tests to run against your own schema, and `RunUntilIdleAsync` for your integration tests. |

Requires .NET 10 and PostgreSQL 14 or later.

## What it promises

1. **Written with the change.** A message commits with the business change or not at all. Writing one outside a transaction is refused.
2. **At least once.** A committed message is attempted until it is done, refused, expired or parked. Handlers must be safe to repeat. The message ID is the same on every attempt, so a receiver can recognise a repeat.
3. **One owner at a time.** A live claim is never given to a second worker. An outcome from a worker whose claim was taken over is refused and changes nothing.
4. **A crash is an attempt.** An attempt that never reports back counts against the limit, so a message that kills its worker ends up parked instead of looping.
5. **Never dropped.** A message that cannot be delivered is parked with the error from each attempt. The library deletes nothing.
6. **No ordering promise.** Messages are claimed by priority, then age, but any two can be handled in either order or at the same time. A handler that cares must check the record's current state.

A message carries no body. It names a record and the kind of work to do on it; the handler loads the record and builds whatever it sends.

## Adopting it

### 1. Add the table to your model

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.AddDurableOutbox(schema: "app", table: "outbox_messages");
}
```

Then generate and apply one migration as you normally would (`dotnet ef migrations add AddOutbox`). The table is yours: the library never creates, alters or deletes from it. Only the schema and table name are your choice; the column names are fixed.

### 2. Register the outbox and a handler

```csharp
services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
services.AddDurableOutbox<AppDbContext>(outbox =>
{
    outbox.RedactError = error => error;   // remove secrets from error text before it is stored or logged
    outbox.AddHandler<SendReceiptHandler>("receipt.send", kind =>
    {
        kind.MaxAttempts = 10;
        kind.AttemptTimeout = TimeSpan.FromMinutes(5);
    });
});
```

Every option has a default, and an invalid one throws when the registration call runs. Set `RunWorkers = false` in a process that only writes messages.

### 3. Write the handler

```csharp
public sealed class SendReceiptHandler(AppDbContext db, ReceiptClient receipts) : IOutboxHandler
{
    public async Task<OutboxResult> HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken)
    {
        var order = await db.Orders.FindAsync([Guid.Parse(delivery.RecordRef)], cancellationToken);
        if (order is null)
        {
            return OutboxResult.Reject("The order no longer exists.");
        }

        // The message ID is the same on every attempt: pass it on so the receiver can drop a repeat.
        await receipts.SendAsync(order, idempotencyKey: delivery.Id, cancellationToken);
        return OutboxResult.Done();
    }
}
```

A handler runs in its own dependency-injection scope per attempt. It returns `Done()`, `Done(record)` for follow-up database writes that commit with the completion, `Retry(error)`, `RetryAt(time, error)` or `Reject(reason)`. An exception counts as a retry. The cancellation token is cancelled when the lease is lost, when the attempt runs past its timeout, and at the end of shutdown: honour it.

### 4. Write messages inside your own transaction

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
db.Orders.Add(order);
await db.SaveChangesAsync(cancellationToken);
await outbox.AddAsync(new OutboxMessage("receipt.send", order.Id.ToString()), cancellationToken);
await transaction.CommitAsync(cancellationToken);
```

`AddAsync` inserts at once inside the transaction you opened, and throws when there is none. The implicit transaction of a lone `SaveChanges` is not enough. A message can also carry a priority, a deadline after which it expires instead of running late, and a uniqueness key: a second message with the same kind and key is not queued.

## Operating it

### Stuck messages

`IOutboxAdmin` is registered with the outbox. The library adds no endpoints; you decide who may call it.

```csharp
var parked = await admin.ListAsync(new OutboxQuery(OutboxMessageStatus.Parked) { Kind = "receipt.send" }, cancellationToken);
await admin.RetryAsync(parked[0].Id, cancellationToken);    // due at once, attempts reset, errors kept
await admin.CancelAsync(parked[1].Id, cancellationToken);   // never attempted again
```

`ListAsync` lists parked or rejected messages, newest first, with each one's error history. Pass the last entry of a page as `After` to get the next.

### Metrics, traces and logs

The meter `DurableOutbox` publishes `durable_outbox.messages.enqueued`, `durable_outbox.attempts` (tagged with how each attempt ended), `durable_outbox.messages.expired`, `durable_outbox.attempt.duration`, and three gauges sampled every 15 seconds: `durable_outbox.messages.pending`, `durable_outbox.messages.parked` and `durable_outbox.oldest_due.age`.

Each attempt runs inside an activity `outbox.handle {kind}` on the activity source `DurableOutbox`, whose parent is the trace that wrote the message, so a trace runs from the request to the delivery. Logs are written under the category `DurableOutbox`, with error text only after your `RedactError`.

```csharp
services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("DurableOutbox"))
    .WithTracing(tracing => tracing.AddSource("DurableOutbox"));
```

### Health check

```csharp
services.AddHealthChecks().AddDurableOutbox<AppDbContext>(degradedAfter: TimeSpan.FromMinutes(5));
```

It reports `Degraded` when a message has waited longer than `degradedAfter` after becoming due, or when the outbox could not be sampled. It never reports `Unhealthy`, because restarting the process does not clear a backlog.

### Start-up check

When the host starts, the library compares the table's columns and indexes with what it expects. A mismatch stops the host with every difference listed. An unreachable database does not: the check is retried, and the workers claim nothing until it passes.

### Things to know

- **Delivery delay.** A new message waits up to 1.2 times `PollInterval` (1 second by default).
- **Table growth.** Finished rows stay until you remove them. Plan a retention job or partitioning.
- **Vacuum.** Each message is updated at least twice. On a busy table, run autovacuum more often than the default.
- **Connections.** Each kind uses up to `MaxConcurrency` connections for handlers plus one for claiming. Size the pool for the sum of every kind, plus your own requests.
- **Slow work.** Kinds do not hold each other up, but messages of one kind share its slots. Give work that must not wait its own kind.
- **Repeated sends.** If the database is unreachable for longer than a lease, another worker can take a message while a handler that ignores its token is still sending. At least once already allows this.
- **Counting.** `messages.enqueued` counts inserts, including those whose transaction later rolls back. A cancelled message's handler is stopped at the next lease renewal, up to a third of the lease later.

The full design, with every decision and its reason, is in [docs/durable-outbox.md](docs/durable-outbox.md).

## Testing your service

### The guarantee tests

`DurableOutbox.Testing` runs the guarantees above against your own `DbContext` and schema, from any test framework. Point it at a database used only for tests, with your migrations applied: it leaves the rows it writes.

```csharp
var contract = new OutboxContract<AppDbContext>(services =>
    services.AddDbContext<AppDbContext>(options => options.UseNpgsql(testConnectionString)));

await contract.WrittenWithTheChangeAsync();
await contract.AtLeastOnceAsync();
await contract.OneOwnerAtATimeAsync();
await contract.CrashIsAnAttemptAsync();
await contract.NeverDroppedAsync();
```

Each method uses its own fake clock and kinds, so it never waits on real time and never touches your messages. A method throws `InvalidOperationException` naming the guarantee when it does not hold.

### Running handlers in an integration test

Register the outbox with `RunWorkers = false` in your test host, then run the handlers until nothing is due:

```csharp
await serviceProvider.RunUntilIdleAsync<AppDbContext>(cancellationToken);
```

It returns once every outcome is written. It does not move the clock, so a message whose retry is in the future is left for later.

## Releasing

Packages are built, tested and packed by `.github/workflows/ci.yml` on every push and pull request. Publishing to nuget.org is a separate workflow, `.github/workflows/publish.yml`, that runs only when started by hand and only after approval. It signs in with [nuget.org Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), so no API key is stored.

Before the first release, the repository owner sets up, once:

1. On nuget.org, a trusted publishing policy for this repository and the workflow file `publish.yml`, with the environment `nuget`.
2. In the GitHub repository settings, an environment named `nuget` with the owner as a required reviewer.
3. A repository variable `NUGET_USER` holding the nuget.org user name (a variable, not a secret).
4. `RepositoryUrl` and `PackageProjectUrl` in `Directory.Build.props`, once the repository URL is known.

The version is `Version` in `Directory.Build.props`. The publish workflow creates no tag.

## Licence

MIT. See [LICENSE](LICENSE).
