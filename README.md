# DurableOutbox

A transactional outbox for .NET 10 services on PostgreSQL, through EF Core and Npgsql.

Write a message in the same database transaction as your business change. Background workers deliver it to a handler you write, retrying until it is done or parked for an operator to look at. No message broker, no extra infrastructure: just a table in the database you already have.

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
db.Orders.Add(order);
await db.SaveChangesAsync(cancellationToken);
await outbox.AddAsync(new OutboxMessage("receipt.send", order.Id.ToString()), cancellationToken);
await transaction.CommitAsync(cancellationToken);
// The order and the message commit together, or neither does.
```

## Why

A service that saves an order and then sends a receipt has a gap between the two. If the process dies after the commit, the receipt is never sent. If it sends first and the commit fails, a receipt goes out for an order that does not exist.

The transactional outbox pattern closes that gap. The intent to send is stored as a row, in the same transaction as the order. A worker picks the row up afterwards and does the work, and keeps trying until it succeeds. DurableOutbox is that row, that worker, and the operator tools around them, for services that already use EF Core on PostgreSQL.

## Features

- **Written with your change.** `AddAsync` joins the transaction you opened and refuses to run without one.
- **At-least-once delivery** with a stable message ID on every attempt, so a receiver can drop repeats.
- **Leases and fencing.** Each claim is time-limited and carries a token. A worker that lost its claim cannot overwrite the outcome of the worker that took over.
- **Crash-safe retries.** An attempt that dies mid-way still counts, so a message that crashes its worker ends up parked rather than looping forever.
- **Per-kind settings:** concurrency, lease length, attempt timeout, attempt limit or retry window, and exponential backoff with jitter.
- **Priority, deadlines and uniqueness keys** on each message.
- **Operator API** to list, retry and cancel parked or rejected messages, with each attempt's error.
- **OpenTelemetry** metrics and traces, with the trace carried from the request that wrote the message to the handler that delivers it.
- **Health check** that reports a backlog, and a **start-up check** that the table matches what the library expects.
- **Testing kit** that runs the guarantees against your own schema, and runs your handlers on demand in integration tests.

## Install

```shell
dotnet add package DurableOutbox
dotnet add package DurableOutbox.Testing   # in your test project, optional
```

| Package | What it contains |
| :--- | :--- |
| `DurableOutbox` | The writer, the workers, the EF Core table mapping, the operator API, telemetry and the health check. |
| `DurableOutbox.Testing` | The guarantee checks to run against your own schema, and `RunUntilIdleAsync` for integration tests. Works with any test framework. |

Requires .NET 10, EF Core 10 with `Npgsql.EntityFrameworkCore.PostgreSQL`, and PostgreSQL 14 or later.

## Getting started

### 1. Add the table to your model

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.AddDurableOutbox(schema: "app", table: "outbox_messages");
}
```

Then add and apply a migration as you normally would:

```shell
dotnet ef migrations add AddOutbox
dotnet ef database update
```

The table is yours. The library never creates, alters or deletes from it. You choose the schema and table name; the column names are fixed.

### 2. Write a handler

A handler does the work for one *kind* of message. A message carries no body: it names a kind and a record, and the handler loads the record and builds whatever it sends.

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

### 3. Register the outbox

```csharp
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddDurableOutbox<AppDbContext>(outbox =>
{
    outbox.RedactError = error => error;   // strip secrets from error text before it is stored or logged
    outbox.AddHandler<SendReceiptHandler>("receipt.send");
});
```

Workers start with the host. Set `RunWorkers = false` in a process that only writes messages.

### 4. Write messages inside your own transaction

Inject `IOutbox` and call `AddAsync` between opening and committing a transaction, as in the example at the top. `AddAsync` inserts the row at once, inside that transaction, and throws `InvalidOperationException` when there is none. The implicit transaction around a lone `SaveChanges` call is not enough.

A message can also carry:

```csharp
var message = new OutboxMessage("receipt.send", order.Id.ToString())
{
    Priority = 10,                                   // higher is claimed first
    Deadline = time.GetUtcNow().AddHours(1),         // expire instead of running late
    UniqueKey = $"receipt:{order.Id}",               // a second message with the same kind and key is not queued
};

bool added = await outbox.AddAsync(message, cancellationToken);   // false when the key already exists
```

## How it works

```text
AddAsync ──► Pending ──► claimed under a lease ──► handler runs
               ▲                                       │
               │   Retry or exception, attempts left   │
               └───────────────────────────────────────┤
                                                       ├── Done ─────────────────► Completed
                                                       ├── Reject ───────────────► Rejected
                                                       └── no attempts left ─────► Parked

Pending ── deadline passes before an attempt ──► Expired
Pending or Parked ── operator cancels ─────────► Cancelled
```

A worker claims a batch of due messages in one statement. A claim does not change the status. It moves the message's due time forward by the lease and stamps a fresh lease token on it, so if the worker dies the message simply becomes due again when the lease runs out. While the handler runs, the worker renews the lease every third of its length.

Every outcome is written only if the row still carries the worker's token. If another worker has taken the message over in the meantime, the late outcome changes nothing.

| Status | Meaning |
| :--- | :--- |
| `Pending` | Waiting to be attempted, or held by an attempt under a lease. |
| `Completed` | A handler returned `Done`. |
| `Rejected` | A handler refused it for good. |
| `Parked` | Its attempts or its retry window ran out. It waits for an operator. |
| `Expired` | Its deadline passed before it was attempted. |
| `Cancelled` | An operator cancelled it. |

Every status but `Pending` is final, unless an operator retries the message.

### Handler results

| Result | What happens |
| :--- | :--- |
| `OutboxResult.Done()` | The message is completed. |
| `OutboxResult.Done(record)` | The message is completed, and `record` runs in the same transaction as the completion, for follow-up writes to your own tables. If the completion is refused, `record` never runs. |
| `OutboxResult.Retry(error)` | Tried again after an exponential backoff, or parked when no attempts are left. |
| `OutboxResult.RetryAt(time, error)` | Tried again at the given time. Uses an attempt like any other retry. |
| `OutboxResult.Reject(reason)` | Refused for good. Not tried again. |
| An exception | Treated as `Retry`, with the exception as the error. |

Each attempt runs in its own dependency-injection scope with a fresh handler. The cancellation token is cancelled when the lease is lost, when the attempt runs past its timeout, and at the end of shutdown. Honour it.

## Guarantees

1. **Written with the change.** A message commits with the business change or not at all.
2. **At least once.** A committed message is attempted until it is completed, rejected, expired or parked. Handlers must be safe to repeat.
3. **One owner at a time.** A live claim is never given to a second worker. An outcome from a worker whose claim was taken over is refused.
4. **A crash is an attempt.** Attempts are counted when a message is claimed, before the handler runs.
5. **Never dropped.** A message that cannot be delivered is parked with the error from each attempt. The library deletes nothing.
6. **No ordering promise.** Messages are claimed by priority, then due time, but any two can be handled in either order or at the same time. A handler that cares must check the record's current state.

Each guarantee is tested against a real PostgreSQL database, and `DurableOutbox.Testing` lets you run the same checks against yours.

## Configuration

Every option has a default, and an invalid value throws `ArgumentException` when `AddDurableOutbox` runs.

```csharp
builder.Services.AddDurableOutbox<AppDbContext>(outbox =>
{
    outbox.PollInterval = TimeSpan.FromSeconds(1);
    outbox.RedactError = error => error;
    outbox.RunWorkers = true;
    outbox.DrainTimeout = TimeSpan.FromSeconds(20);

    outbox.AddHandler<SendReceiptHandler>("receipt.send", kind =>
    {
        kind.Lease = TimeSpan.FromSeconds(60);
        kind.MaxConcurrency = 8;
        kind.AttemptTimeout = TimeSpan.FromMinutes(5);
        kind.MaxAttempts = 10;
        kind.RetryWindow = null;
        kind.BackoffBase = TimeSpan.FromSeconds(5);
        kind.BackoffCap = TimeSpan.FromMinutes(15);
    });
});
```

| Option | Default | Description |
| :--- | :--- | :--- |
| `PollInterval` | 1 second | How long a worker waits before looking again when nothing was due. Jittered by ±20%. |
| `RedactError` | unchanged | Applied to every error text before it is stored or logged. Use it to remove secrets. |
| `RunWorkers` | `true` | Whether this process runs handlers. Set `false` in a process that only writes. |
| `DrainTimeout` | 20 seconds | How long shutdown waits for running handlers before cancelling their tokens. |
| `Lease` | 60 seconds | How long a claim lasts without renewal. Must be much longer than the clock difference between your instances. |
| `MaxConcurrency` | 8 | Attempts of this kind running at once in one process. |
| `AttemptTimeout` | 5 minutes | When an attempt's token is cancelled and it is recorded as a retry. `null` turns it off. |
| `MaxAttempts` | 10 | Attempts before the message is parked. |
| `RetryWindow` | none | Optionally, also park a message once it is this old. |
| `BackoffBase`, `BackoffCap` | 5 seconds, 15 minutes | The retry delay is `min(base × 2^(attempt − 1), cap)`, jittered by ±20%. |

A process claims only the kinds it has a handler for. Messages of any other kind wait untouched.

## Operating it

### Fixing stuck messages

`IOutboxAdmin` is registered with the outbox. The library adds no HTTP endpoints; you decide who may call it.

```csharp
var parked = await admin.ListAsync(
    new OutboxQuery(OutboxMessageStatus.Parked) { Kind = "receipt.send" }, cancellationToken);

await admin.RetryAsync(parked[0].Id, cancellationToken);    // due at once, attempts reset, errors kept
await admin.CancelAsync(parked[1].Id, cancellationToken);   // never attempted again
```

`ListAsync` returns parked or rejected messages, most recently finished first, with up to the last 20 errors each. Pass the last entry of a page as `After` to get the next page.

### Metrics and traces

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter("DurableOutbox"))
    .WithTracing(tracing => tracing.AddSource("DurableOutbox"));
```

| Instrument | Type | Tags |
| :--- | :--- | :--- |
| `durable_outbox.messages.enqueued` | Counter | `kind` |
| `durable_outbox.attempts` | Counter | `kind`, `outcome` (`completed`, `retry`, `timed_out`, `rejected`, `parked`, `lease_lost`, `abandoned`) |
| `durable_outbox.messages.expired` | Counter | `kind` |
| `durable_outbox.attempt.duration` | Histogram, seconds | `kind`, `outcome` |
| `durable_outbox.messages.pending` | Gauge | `kind` |
| `durable_outbox.messages.parked` | Gauge | `kind` |
| `durable_outbox.oldest_due.age` | Gauge, seconds | `kind` |

The gauges are sampled every 15 seconds. Each attempt runs inside a consumer activity named `outbox.handle {kind}`, whose parent is the trace that wrote the message. Logs use the category `DurableOutbox`, and error text appears only after `RedactError`.

### Health check

```csharp
builder.Services.AddHealthChecks()
    .AddDurableOutbox<AppDbContext>(degradedAfter: TimeSpan.FromMinutes(5));
```

Reports `Degraded` when a message has waited longer than `degradedAfter` after becoming due, or when the outbox could not be sampled. It never reports `Unhealthy`, because restarting the process does not clear a backlog.

### Start-up check

When the host starts, the library compares the table's columns and indexes with what it expects. A mismatch stops the host and lists every difference. An unreachable database does not: the check is retried, and workers claim nothing until it passes.

### Production notes

- **Delivery delay.** A new message waits up to 1.2 × `PollInterval`. The library does not use `LISTEN/NOTIFY`.
- **Table growth.** Finished rows stay until you remove them. Plan a retention job or partitioning.
- **Vacuum.** Each message is updated at least twice. On a busy table, run autovacuum more often than the default.
- **Connections.** Each kind uses up to `MaxConcurrency` connections for handlers, plus one for claiming. Size the pool for the sum across kinds, plus your own requests.
- **Slow work.** Kinds do not hold each other up, but messages of one kind share its slots. Give work that must not wait its own kind.
- **Repeated sends.** If the database is unreachable for longer than a lease, another worker can take a message while a handler that ignores its token is still sending. At-least-once delivery already allows this; make receivers idempotent.
- **Upgrades.** When a release changes the table's indexes, add one migration. The start-up check tells you what is missing.

## Testing your service

### Check the guarantees against your schema

`OutboxContract<TContext>` runs the guarantees against your own `DbContext`, from any test framework. Point it at a database used only for tests, with your migrations applied; it leaves the rows it writes.

```csharp
var contract = new OutboxContract<AppDbContext>(services =>
    services.AddDbContext<AppDbContext>(options => options.UseNpgsql(testConnectionString)));

await contract.WrittenWithTheChangeAsync();
await contract.AtLeastOnceAsync();
await contract.OneOwnerAtATimeAsync();
await contract.CrashIsAnAttemptAsync();
await contract.NeverDroppedAsync();
```

Each check uses its own fake clock and its own message kinds, so it never waits on real time and never touches your messages. A check that fails throws `InvalidOperationException` naming the guarantee.

### Run your handlers in an integration test

Register the outbox with `RunWorkers = false` in your test host, then run the handlers until nothing is due:

```csharp
await serviceProvider.RunUntilIdleAsync<AppDbContext>(cancellationToken);
```

It returns once every outcome is written. It uses the registered `TimeProvider` and does not move it, so a message whose retry is in the future is left for later.

## What it is not

- **Not a message bus.** There is no transport and no broker. Your handler calls whatever it needs to.
- **No message body.** A message names a record; the handler reads the current state of that record.
- **No ordering.** See guarantee 6.
- **No other databases.** PostgreSQL only.
- **No schema management.** The table lives in your EF Core model and your migrations.

## Documentation

- [Design](docs/durable-outbox.md): the guarantees, every decision and its reason, and the full technical design.
- [Build plan](docs/build-plan.md): the order of work for version 1.
- [Releasing](docs/releasing.md): how packages are published.

## Building from source

```shell
dotnet build
dotnet test
```

The tests run against real PostgreSQL in Testcontainers, so Docker must be running.

## Licence

MIT. See [LICENSE](LICENSE).
