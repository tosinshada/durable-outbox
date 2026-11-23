# Durable outbox library for .NET

| | |
| :--- | :--- |
| Status | Part 1 (feature proposal, sections 1 to 8) and Part 2 (technical design, section 9) are agreed. Nothing is built or verified yet. |
| Date | 2026-11-23 |
| Next step | Prove the claim statement against PostgreSQL, then create the repository. |

## 1. Summary

A small .NET library that implements the transactional outbox pattern on PostgreSQL. A service writes a message in the same database transaction as its business change. Background workers then claim each message under a lease and hand it to a handler the service wrote, retrying until it is done or parked for an operator.

It is an outbox and a worker. It is not a message bus, and it needs no broker.

## 2. The problem

A service changes its own data and must also cause an effect somewhere else: call another system, send a notification, file a report. If the two are separate steps, a crash between them either loses the effect or produces one for a change that never committed.

The outbox pattern fixes this by recording the intent in the same transaction as the change, and carrying it out afterwards. Writing the row is the easy part. The parts that teams get wrong when they build it themselves are:

- Two workers sending the same message, or a worker that lost its claim overwriting the result of the one that took over.
- A message that crashes its worker being retried forever.
- A slow batch outliving its claim, so another instance sends it again.
- Messages silently dropped after the last retry, with no record of why.
- No way to see what is stuck, how long it has been stuck, or to send it again.

## 3. Why build it

| Option | Finding | Verdict |
| :--- | :--- | :--- |
| MassTransit | Version 9 moves to a commercial licence; version 8 stays open source with security patches. Its EF Core outbox exists to relay messages to a broker. | Not a fit without a broker, and adds a licence decision. |
| Wolverine | Has PostgreSQL message storage, a PostgreSQL transport and EF Core integration. It is a full messaging framework with its own handler model and runtime. | Capable, but far larger than the problem. Decided: build. |
| CAP | Requires both a storage provider and a message transport. | Not a fit without a broker. |

The gap is a library that does only this: a table the service owns, a claim that is safe across instances, and a strict contract about retries and failure.

## 4. What the research says a good one does

| Topic | Finding | Effect on the proposal |
| :--- | :--- | :--- |
| Claiming | Established Postgres queues (River, Oban, pg-boss) all claim with `FOR UPDATE SKIP LOCKED` in one statement, so workers never wait on each other. | The claim is one statement. |
| Abandoned work | They recover work from dead workers by lease expiry or heartbeat. River's rescuer once overwrote jobs that had finished in the meantime. | Every claim carries a token, and an outcome is accepted only from the current token. |
| Wake-up | `LISTEN/NOTIFY` is used only as a hint, with polling as the fallback. It does not work through PgBouncer in transaction mode, and a commit that notifies takes a global lock whose cost is disputed. | Polling only in version 1. |
| Reading by cursor | A relay that reads "ID greater than the last one seen" loses rows, because IDs are assigned before commit and become visible out of order. | Messages are claimed by state, never by cursor. |
| Queue index | A partial index that holds only rows still to be sent, in the order they are claimed, keeps the claim cheap as the table grows. | The table definition includes it. |
| Hot tables | Queue tables that delete finished rows suffer from dead rows and vacuum pressure. | The library never deletes. Retention is the service's choice. |

Sources are listed in section 10.

## 5. Proposal

### 5.1 What it is

A NuGet package named `DurableOutbox` (with a companion test kit, `DurableOutbox.Testing`), public on nuget.org under the MIT licence, for .NET 10 services that use PostgreSQL through EF Core and Npgsql. A service:

1. Registers the library and applies its EF Core configuration to its own `DbContext`.
2. Creates the table through its own migrations.
3. Writes messages inside its own transactions.
4. Registers one handler per message kind.

The library runs the workers and enforces the guarantees below.

### 5.2 What a message is

A message names a record and says what kind of work to do on it. It carries no body.

| Field | Meaning |
| :--- | :--- |
| Kind | Which handler runs. |
| Record reference | The identifier of the record the handler acts on. The handler loads the record and builds whatever it sends. |
| Priority | Higher is claimed first; earliest due first within a priority. |
| Deadline | Optional. A message not done by then expires instead of being processed late. |
| Uniqueness key | Optional. A second message with the same key is not queued. |

Because the message holds only a reference, it never stores a secret or personal data, and the handler always works from current data. The cost is on the handler: if the receiver must see exactly the same content on every attempt, the service stores that content in its own record and the message points to it.

### 5.3 Guarantees

These are the contract. Each one is proved by a test against real PostgreSQL.

1. **Written with the change.** A message commits with the business change or not at all. Writing one outside a transaction is refused.
2. **At least once.** A committed message is attempted until it is done, refused, expired or parked. Handlers must be safe to repeat. The message ID is the same on every attempt, so a receiver can recognise a repeat.
3. **One owner at a time.** A live claim is never given to a second worker. An outcome from a worker whose claim was taken over is refused and changes nothing.
4. **A crash is an attempt.** An attempt that never reports back counts against the limit, so a message that kills its worker ends up parked instead of looping.
5. **Never dropped.** A message that cannot be delivered is parked with the error from each attempt. The library deletes nothing.
6. **No ordering promise.** Messages are claimed by priority, then age, but any two messages can be handled in either order or at the same time, including two for the same record. A handler that cares must check the record's current state.

### 5.4 Features in version 1

| Area | Feature |
| :--- | :--- |
| Writing | Add a message to the caller's open transaction. Refused when none is open. Duplicate uniqueness keys are ignored. |
| Claiming | One statement claims a batch of due messages for a lease, with a token. Safe for any number of instances. |
| Handlers | One per kind. Each attempt runs in its own dependency-injection scope. A batch is handled concurrently up to a limit per kind. |
| Outcomes | Done; done with follow-up database writes that commit together with the completion; retry; retry at a given time; refused for good. |
| Leases | Renewed while a handler is still running, so long work is not taken over. Time comes from `TimeProvider`. |
| Timeouts | Per kind, an attempt that runs too long is cancelled and retried, so a hung handler cannot hold a slot forever. |
| Retries | Per kind: attempt limit or retry window, exponential backoff with jitter and a cap. The attempt limit is enforced when claiming. |
| Failure | Parked and expired states. An error is kept per attempt, passed through a hook the service supplies so sensitive text is not stored. |
| Operators | In code: list parked messages, retry one, cancel one. |
| Shutdown | Stop claiming, let running handlers finish within the host's timeout, and always record an outcome that was reached. |
| Observability | Metrics for queue depth, age of the oldest due message, attempts, failures, parked and expired. Trace context carried from the writer to the handler. Structured logs. A health check for "oldest due message is too old". |
| Schema | EF Core configuration the service applies to its own model. A start-up check that the table matches what the library expects. |
| Testing | The contract tests are published as a kit, so a service can run them against its own schema. |

### 5.5 Later, not version 1

- Wake-up by `LISTEN/NOTIFY`.
- Helpers for partitioning the table by month.
- A generic inbox for de-duplicating received messages.

### 5.6 Out of scope

- A message bus, publish and subscribe, sagas, or scheduling of recurring jobs.
- Any transport. The library never calls anything outside the database; handlers do.
- Webhook sending, signing or any other delivery code.
- Message bodies. A message is a reference to a record.
- Databases other than PostgreSQL, and data access other than EF Core.
- Exactly-once delivery.
- Ordering of messages, globally or per key.
- An admin screen. The library exposes the operations; a screen belongs to the service.

## 6. Decisions made

| # | Decision | Outcome |
| :--- | :--- | :--- |
| 1 | Build or adopt a framework | Build. |
| 2 | Inbox | Outbox only. |
| 3 | Webhook sender | Not included. |
| 4 | Payload | A reference to a record only. |
| 5 | Ordering per key | Not included, and no column reserved for it. |
| 6 | Clock for leases | The application clock through `TimeProvider`. |
| 7 | Home and distribution | Its own repository, published to NuGet. |
| 8 | .NET versions | .NET 10 only. |
| 9 | Table ownership | The service, through its own migrations. The library supplies the EF Core configuration. |
| 10 | Package name | `DurableOutbox`. |
| 11 | Distribution and licence | Public on nuget.org, MIT. |

## 7. How we know it worked

- Every guarantee in 5.3 has a passing test against real PostgreSQL.
- Killing a worker mid-batch while a second instance runs produces no lost message and no accepted outcome from the dead worker's claim.
- A message that crashes its handler every time is parked after its attempt limit.
- A service can adopt it with a registration call, one migration and one handler.

## 8. Open questions

None. Every question raised so far is settled in section 6.

## 9. Part 2: technical design

Code and SQL here show the intended shape; names can still change during implementation.

### 9.1 Packages

| Package | Contents | Depends on |
| :--- | :--- | :--- |
| `DurableOutbox` | Writer, workers, EF Core configuration, operator API, telemetry, health check | `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Diagnostics.HealthChecks` |
| `DurableOutbox.Testing` | The contract tests and a drain helper for a service's own tests | `DurableOutbox` |

Both target `net10.0` only. PostgreSQL 14 or later.

### 9.2 The central design choice: a claim is a postponed due time

A message has one due time, `next_attempt_at`. Claiming a message moves that time forward by the lease and stamps a fresh token on the row. The row stays `Pending`.

This gives three properties with no extra machinery:

- **Recovery is automatic.** A message whose worker died becomes due again when the lease runs out. There is no "in flight" state to sweep.
- **One index serves everything.** Fresh, retrying and abandoned messages are all "pending and due".
- **Fencing is one condition.** Every outcome is written `WHERE id = … AND lease_token = …`. A worker whose claim was taken over matches no row.

### 9.3 Table

The service creates this through its own migrations, from the EF Core configuration the library supplies. Schema and table name are the service's choice. Column names are fixed by the configuration, so the library's SQL does not depend on the service's naming convention.

| Column | Type | Notes |
| :--- | :--- | :--- |
| `id` | `uuid` | Primary key. UUID version 7, generated by the library. Sent to handlers as the message ID. |
| `kind` | `text` | Which handler runs. |
| `record_ref` | `text` | The record the handler acts on. |
| `unique_key` | `text` null | Optional. Unique with `kind`. |
| `priority` | `integer` | Default 0. Higher first. |
| `status` | `text` | `Pending`, `Completed`, `Rejected`, `Parked`, `Expired`, `Cancelled`. |
| `attempts` | `integer` | Incremented when claimed, before the handler runs. |
| `next_attempt_at` | `timestamptz` | When it is next due. While claimed, when the lease ends. |
| `lease_token` | `uuid` null | Set while claimed. Null when waiting or finished. |
| `deadline_at` | `timestamptz` null | Optional. |
| `created_at` | `timestamptz` | |
| `finished_at` | `timestamptz` null | Set on any final status. |
| `last_error` | `text` null | The most recent error. |
| `errors` | `jsonb` | Array of `{attempt, at, error}`, the most recent 20. |
| `trace_parent` | `text` null | W3C trace context of the writer. |

Indexes:

| Index | Purpose |
| :--- | :--- |
| `(kind, priority DESC, next_attempt_at) WHERE status = 'Pending'` | The claim. Holds only unfinished messages, in claim order. |
| `UNIQUE (kind, unique_key) WHERE unique_key IS NOT NULL` | Uniqueness. |
| `(kind, finished_at) WHERE status IN ('Parked', 'Rejected')` | Operator lists and the parked gauge. |

Status changes:

```
Pending ──► Completed
        ──► Rejected   (handler refused it for good)
        ──► Parked     (attempts or retry window used up)
        ──► Expired    (deadline passed)
        ──► Cancelled  (operator)
Parked, Rejected ──► Pending    (operator retry)
Parked           ──► Cancelled  (operator)
```

### 9.4 Writing a message

```csharp
public interface IOutbox
{
    /// Returns false when a message with the same kind and unique key already exists.
    Task<bool> AddAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}

public sealed record OutboxMessage(string Kind, string RecordRef)
{
    public int Priority { get; init; }
    public DateTimeOffset? Deadline { get; init; }
    public string? UniqueKey { get; init; }
}
```

`AddAsync` runs one `INSERT … ON CONFLICT (kind, unique_key) WHERE unique_key IS NOT NULL DO NOTHING` on the `DbContext`'s connection, at once, inside the transaction that is already open. It throws `InvalidOperationException` when the `DbContext` has no current or enlisted transaction.

Two consequences:

- The insert is not a tracked entity, so a duplicate does not fail the service's `SaveChanges`.
- A service that relies on the implicit transaction of a single `SaveChanges` must open one explicitly to use the outbox. This is point 1 in 9.14.

Uniqueness lasts as long as the row exists. Because the library never deletes, that is until the service removes the row.

### 9.5 Handlers

```csharp
public interface IOutboxHandler
{
    Task<OutboxResult> HandleAsync(OutboxDelivery delivery, CancellationToken cancellationToken);
}

public sealed record OutboxDelivery(
    Guid Id, string Kind, string RecordRef, int Attempt,
    DateTimeOffset CreatedAt, DateTimeOffset? Deadline);

public abstract record OutboxResult
{
    public static OutboxResult Done();
    public static OutboxResult Done(Func<CancellationToken, Task> record);
    public static OutboxResult Retry(string error);
    public static OutboxResult RetryAt(DateTimeOffset at, string error);
    public static OutboxResult Reject(string reason);
}
```

- Each attempt runs in its own dependency-injection scope.
- An exception counts as `Retry`. Its text passes through the service's error filter before it is stored, and is cut to 2,000 characters.
- The cancellation token is cancelled when the lease is lost, and at the end of the drain period on shutdown. Nothing is recorded for an attempt cancelled this way.
- The token is also cancelled when the attempt runs longer than the kind's `AttemptTimeout`. The handler then has 5 seconds to return. After that the attempt is recorded as a retry with the error "timed out", and its slot is freed whether or not the handler has returned. A handler that ignores the token keeps running outside the concurrency limit, and its late result is refused, so handlers must honour the token.
- `RetryAt` uses an attempt like any other retry.
- `Done(record)` is for follow-up database writes. The library opens a transaction on the scope's `DbContext`, writes the fenced completion first, then runs `record`, saves and commits. If the completion matches no row, `record` never runs. Under a retrying execution strategy `record` can run more than once, so it must only touch the database.

### 9.6 Registration

```csharp
services.AddDurableOutbox<AppDbContext>(outbox =>
{
    outbox.PollInterval = TimeSpan.FromSeconds(1);
    outbox.RedactError = error => error;          // the service's filter for stored error text
    outbox.RunWorkers = true;                     // false in a process that only writes

    outbox.AddHandler<SendReceiptHandler>("receipt.send", kind =>
    {
        kind.Lease = TimeSpan.FromSeconds(60);
        kind.MaxConcurrency = 8;
        kind.AttemptTimeout = TimeSpan.FromMinutes(5);   // null turns it off
        kind.MaxAttempts = 10;
        kind.RetryWindow = null;                  // optional: also give up by age
        kind.BackoffBase = TimeSpan.FromSeconds(5);
        kind.BackoffCap = TimeSpan.FromMinutes(15);
    });
});

// In the service's DbContext:
protected override void OnModelCreating(ModelBuilder modelBuilder) =>
    modelBuilder.AddDurableOutbox(schema: "app", table: "outbox_messages");
```

Options are validated at start-up. The schema and table name are read from the EF Core model, so the SQL is built once and always matches the migration.

### 9.7 Claiming

One worker loop per kind. Each claim is a single statement, sent on its own with no surrounding transaction:

```sql
WITH due AS MATERIALIZED (
    SELECT id,
           CASE
               WHEN deadline_at <= @now THEN 'Expired'
               WHEN attempts >= @max_attempts OR created_at <= @give_up_before THEN 'Parked'
               ELSE 'Pending'
           END AS verdict,
           lease_token IS NOT NULL AS abandoned
      FROM app.outbox_messages
     WHERE status = 'Pending' AND kind = @kind AND next_attempt_at <= @now
     ORDER BY priority DESC, next_attempt_at
     LIMIT @limit
       FOR UPDATE SKIP LOCKED
)
UPDATE app.outbox_messages m
   SET status          = due.verdict,
       attempts        = m.attempts + (due.verdict = 'Pending')::int,
       lease_token     = CASE WHEN due.verdict = 'Pending' THEN @token END,
       next_attempt_at = CASE WHEN due.verdict = 'Pending' THEN @now + @lease ELSE m.next_attempt_at END,
       finished_at     = CASE WHEN due.verdict <> 'Pending' THEN @now END,
       last_error      = CASE WHEN due.abandoned THEN @abandoned_error ELSE m.last_error END,
       errors          = CASE WHEN due.abandoned
                              THEN m.errors || jsonb_build_object('attempt', m.attempts, 'at', @now, 'error', @abandoned_error)
                              ELSE m.errors END
  FROM due
 WHERE m.id = due.id
RETURNING m.id, m.record_ref, m.status, m.attempts, m.created_at, m.deadline_at, m.trace_parent;
```

What the one statement does:

- **Claims** due messages for this kind, skipping rows another worker has locked.
- **Parks** a message that is due again with its attempts used up. That only happens when its last attempt never reported, which is how a crash loop ends (guarantee 4).
- **Expires** a message past its deadline. A message is checked when it becomes due, so one that is mid-attempt when its deadline passes is allowed to finish.
- **Records** an abandoned attempt in the error history.

`@limit` is the number of free concurrency slots. Rows returned as `Parked` or `Expired` are counted and logged, not handled.

### 9.8 Outcomes and renewal

Every outcome is one fenced statement, written with no cancellation token:

```sql
-- Done
UPDATE app.outbox_messages
   SET status = 'Completed', finished_at = @now, lease_token = NULL
 WHERE id = @id AND lease_token = @token AND status = 'Pending';

-- Retry (or RetryAt)
UPDATE app.outbox_messages
   SET next_attempt_at = @next, lease_token = NULL, last_error = @error, errors = errors || @entry
 WHERE id = @id AND lease_token = @token AND status = 'Pending';
```

`Reject` and a retry with attempts or the retry window used up set `Rejected` or `Parked` in the same way. One row affected means the outcome stands. None means the lease was lost: the result is discarded, logged and counted.

The retry delay is `min(base × 2^(attempt − 1), cap)`, multiplied by a random factor between 0.8 and 1.2.

Renewal runs per kind every third of the lease and extends every message in flight in one statement:

```sql
UPDATE app.outbox_messages
   SET next_attempt_at = @now + @lease
 WHERE id = ANY(@ids) AND lease_token = @token AND status = 'Pending'
RETURNING id;
```

A message missing from the result has lost its lease, and its handler's token is cancelled.

### 9.9 Worker loop

For each kind:

1. Wait until at least one concurrency slot is free.
2. Claim up to that many messages.
3. Start each on its own task and scope. On finish, write the outcome and free the slot.
4. If the claim filled every free slot, go again as soon as a slot frees. Otherwise wait `PollInterval`, with jitter.
5. If the database is unreachable, log, and wait with a backoff of up to 30 seconds. Nothing is lost: claims expire on their own.

Because each kind has its own loop and its own limit, a slow kind cannot hold up another.

On shutdown the loop stops claiming and waits for running handlers. After `DrainTimeout` (default 20 seconds, inside the host's 30) their tokens are cancelled. Anything still unfinished is recovered by lease expiry.

All times come from `TimeProvider`. A lease must be much longer than the clock difference between instances. A clock difference can cause an early takeover and so a repeated send, which at-least-once already allows; the token still prevents a wrong write.

### 9.10 Operator API

```csharp
public interface IOutboxAdmin
{
    Task<IReadOnlyList<OutboxMessageInfo>> ListAsync(OutboxQuery query, CancellationToken cancellationToken);
    Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken);   // Parked or Rejected → Pending, attempts reset, errors kept
    Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken);  // Pending or Parked → Cancelled
}
```

`ListAsync` filters by status and kind and pages by `(finished_at, id)`. Cancelling a message that is mid-attempt clears its token, so that attempt's outcome is refused. The library adds no endpoints; the service decides who may call these.

### 9.11 Telemetry and health

Meter `DurableOutbox`:

| Instrument | Type | Tags |
| :--- | :--- | :--- |
| `durable_outbox.messages.enqueued` | Counter | `kind` |
| `durable_outbox.attempts` | Counter | `kind`, `outcome` (`completed`, `retry`, `timed_out`, `rejected`, `parked`, `lease_lost`, `abandoned`) |
| `durable_outbox.messages.expired` | Counter | `kind` |
| `durable_outbox.attempt.duration` | Histogram, seconds | `kind`, `outcome` |
| `durable_outbox.messages.pending` | Gauge | `kind` |
| `durable_outbox.messages.parked` | Gauge | `kind` |
| `durable_outbox.oldest_due.age` | Gauge, seconds | `kind` |

The three gauges come from one grouped query over the partial indexes, run every 15 seconds by default.

Tracing uses the activity source `DurableOutbox`. `AddAsync` stores the current trace context on the row. Each attempt starts a consumer activity `outbox.handle {kind}` with that context as its parent, so a trace runs from the request to the delivery.

Logs use source-generated messages with the message ID, kind and attempt. The record reference is logged; error text is logged only after the service's filter.

The health check reports `Degraded` when the oldest due message of any kind is older than a threshold the service sets. It reads the sampled gauge and runs no query of its own. It never reports `Unhealthy`: a backlog is not fixed by restarting the process.

At start-up the library compares the table's columns and indexes with what it expects and fails fast on a mismatch.

### 9.12 Testing

The library's own tests run against PostgreSQL in Testcontainers, with a fake `TimeProvider`. One or more per guarantee:

| Guarantee | Test |
| :--- | :--- |
| 1 | A commit that fails leaves neither the change nor the message. `AddAsync` with no transaction throws. |
| 2 | A message is attempted until done, with the same ID each time. A duplicate unique key is not queued. |
| 3 | Four workers claim 1,000 messages exactly once. A completion with a stale token changes nothing. A renewed lease is not taken over. |
| 4 | An attempt that never reports is claimed again after its lease, and parked after the attempt limit. |
| 5 | A message that fails every attempt is parked with each error. An operator retry makes it claimable. |
| Slow work | An attempt past its timeout is cancelled, recorded as a retry and its slot freed, including when the handler ignores the token. A kind with every slot held does not delay another kind. |
| Other | Priority order, deadline expiry, backoff timing, `Done(record)` committing with the completion and rolling back when the lease is lost, drain on shutdown, recovery after the database restarts. |

`DurableOutbox.Testing` publishes the guarantee tests as methods a service calls against its own `DbContext` and schema, with no dependency on a test framework. It also has `RunUntilIdleAsync`, which runs the workers until nothing is due, for a service's integration tests.

### 9.13 Operating notes and known limits

- **Delivery delay.** A new message waits up to `PollInterval` (1 second by default).
- **Repeated sends.** If renewal fails for longer than the lease, for example while the database is unreachable, another worker can take the message while the first handler is still sending.
- **Table growth.** Finished rows stay until the service removes them. They are not in the claim index, so they do not slow claiming, but the service needs a retention job or partitioning.
- **Vacuum.** Each message is updated at least twice, and those updates touch an indexed column. On a busy table, autovacuum should be set to run more often than the default.
- **Connections.** Each kind uses up to `MaxConcurrency` connections for handlers, plus one for claiming and renewal. All kinds share the service's connection pool, so the pool must cover the sum of every kind's `MaxConcurrency`, plus one per kind, plus what the service's own requests need. A pool smaller than that lets one busy kind starve the others and the service itself.
- **Slow work within a kind.** Kinds are isolated from each other, but messages of one kind share its slots. If a kind sends to several destinations and one is slow, that destination can hold every slot. Register a separate kind for work that must not be held up.

### 9.14 Design decisions made

| # | Decision | Reason |
| :--- | :--- | :--- |
| 1 | `AddAsync` needs an explicit transaction and inserts at once. The implicit transaction of a lone `SaveChanges` is not enough. | It is what makes "duplicate ignored" and "refused outside a transaction" possible. |
| 2 | Column names are fixed by the library. Only the schema and table name are the service's choice. | It keeps the SQL constant and the start-up check simple. |
| 3 | An attempt's trace is a child of the writer's trace, including retries that run much later. | A link would be more exact for late retries but makes the common case harder to follow. |
| 4 | Defaults: poll 1 second, lease 60 seconds, attempt timeout 5 minutes, 10 attempts, backoff 5 seconds to 15 minutes. | Starting values, all configurable. |

### 9.15 Not yet verified

The SQL and API in this section are a design. None of it has been run. The claim statement in 9.7 is the part to prove first against real PostgreSQL, under several concurrent workers.

## 10. Sources

- [Announcing MassTransit v9](https://masstransit.io/introduction/v9-announcement)
- [MassTransit SQL transport](https://masstransit.io/documentation/transports/sql)
- [Wolverine PostgreSQL integration](https://wolverine.netlify.app/guide/durability/postgresql)
- [CAP event bus and the outbox pattern](https://codeopinion.com/cap-event-bus-outbox-pattern/)
- [River: a Postgres job queue for Go](https://www.brandur.org/river)
- [River release notes (job rescuer fix)](https://newreleases.io/project/github/riverqueue/river/release/v0.48.0)
- [Oban troubleshooting: PgBouncer and LISTEN/NOTIFY](https://hexdocs.pm/oban/troubleshooting.html)
- [Postgres LISTEN/NOTIFY does not scale (Recall.ai)](https://recall.ai/blog/postgres-listen-notify-does-not-scale)
- [Postgres LISTEN/NOTIFY can actually scale (DBOS)](https://dbos.dev/blog/postgres-listen-notify-scalability)
- [Ordering in a Postgres outbox (Oskar Dudycz)](https://event-driven.io/en/ordering_in_postgres_outbox/)
- [Fun with MVCC: the sequence gap problem](https://elucid.github.io/posts/fun-with-mvcc)
