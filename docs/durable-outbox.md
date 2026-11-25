# Durable outbox library for .NET

| | |
| :--- | :--- |
| Status | Part 1 (feature proposal, sections 1 to 8) and Part 2 (technical design, section 9) are agreed. Version 1 is built and tested against PostgreSQL 17: the table, the writer, the claim, the fenced outcomes, the handlers, the workers, the operator API, telemetry, the health check, the start-up check and the test kit. Version 1.0.0 of `DurableOutbox` and of the test kit, `DurableOutbox.Testing`, is published on nuget.org, and later packs of both are checked against it for breaking changes. Version 1.1.0 adds the give-up hook (9.5), built and tested the same way; `DurableOutbox` 1.1.0 is published, and the kit's 1.1.0 is packed from the same source and waits to be published (section 6, decision 12). Version 1.2.0 makes the library work under an EF Core naming convention (9.3) and makes `RunUntilIdleAsync` stop on a failed claim and handle messages that one kind adds for another (9.12); it is built and tested, and not yet published. Section 9.15 has the detail. |
| Date | 2026-11-25 |
| Next step | None set. Later work (section 5.5) goes through OpenSpec and is released by the steps in `docs/releasing.md`. |

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
| Failure | Parked and expired states. An error is kept per attempt, passed through a hook the service supplies so sensitive text is not stored. Since 1.1.0, a kind's handler can be told when the library gives up on a message, and commit its own writes with the new status. |
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
| 12 | Test kit distribution | `DurableOutbox.Testing` is published on nuget.org beside `DurableOutbox`, at the same version, with every release. Services need it to run the guarantees against their own schema and to run their handlers in integration tests (9.12). This reverses an earlier form of this decision, made after 1.0.0, that kept the kit in the repository only; `DurableOutbox` 1.1.0 was published alone under it, so the kit's 1.1.0 is published afterwards from the same source. |

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
| `DurableOutbox.Testing` | The contract tests and a drain helper for a service's own tests, also used by the library's own tests | `DurableOutbox` (the exact same version, because the kit uses its internals), `Microsoft.Extensions.TimeProvider.Testing` (the clock the contract tests move) |

Both target `net10.0` only. PostgreSQL 14 or later.

### 9.2 The central design choice: a claim is a postponed due time

A message has one due time, `next_attempt_at`. Claiming a message moves that time forward by the lease and stamps a fresh token on the row. The row stays `Pending`.

This gives three properties with no extra machinery:

- **Recovery is automatic.** A message whose worker died becomes due again when the lease runs out. There is no "in flight" state to sweep.
- **One index serves everything.** Fresh, retrying and abandoned messages are all "pending and due".
- **Fencing is one condition.** Every outcome is written `WHERE id = … AND lease_token = …`. A worker whose claim was taken over matches no row.

### 9.3 Table

The service creates this through its own migrations, from the EF Core configuration the library supplies. Schema and table name are the service's choice. Column names are fixed by the configuration, so the library's SQL does not depend on the service's naming convention. The same holds for what the library reads back: a naming convention added to the service's `DbContext` (for example `UseSnakeCaseNamingConvention()` from the EFCore.NamingConventions package) also applies to the internal types the claim, the gauges, the operator list and the start-up check read their rows into, so every property of those types names its result column itself, which a convention does not override (since 1.2.0).

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

| Index | Name | Purpose |
| :--- | :--- | :--- |
| `(kind, priority DESC, next_attempt_at) WHERE status = 'Pending'` | `ix_{table}_claim` | The claim, the pending gauge and the oldest due age. Holds only unfinished messages, in claim order. |
| `UNIQUE (kind, unique_key) WHERE unique_key IS NOT NULL` | `ux_{table}_unique_key` | Uniqueness. |
| `(kind, finished_at) WHERE status IN ('Parked', 'Rejected')` | `ix_{table}_finished` | Operator lists and the parked gauge. |

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

public abstract class OutboxResult      // only the library creates results
{
    public static OutboxResult Done();
    public static OutboxResult Done(Func<CancellationToken, Task> record);
    public static OutboxResult Retry(string error);
    public static OutboxResult RetryAt(DateTimeOffset at, string error);
    public static OutboxResult Reject(string reason);
}

public interface IOutboxGiveUpHandler   // optional, since 1.1.0: implemented by a kind's handler class
{
    Task OnGiveUpAsync(OutboxDelivery delivery, OutboxGiveUpReason reason, CancellationToken cancellationToken);
}

public enum OutboxGiveUpReason { AttemptsUsedUp, RetryWindowPassed, DeadlinePassed }
```

- Each attempt runs in its own dependency-injection scope, with a handler resolved from it.
- An exception counts as `Retry`. Its stored text is the exception's type, message and stack trace.
- Every error text (a `Retry` or `RetryAt` error, a `Reject` reason, an exception, "timed out") passes once through the service's error filter, `RedactError`, and is then cut to 2,000 characters. That one text is what is stored and what is logged. If the filter throws, the library stores and logs "The error text could not be redacted." instead, never the original.
- The cancellation token is cancelled when the lease is lost (9.8), and at the end of the drain period on shutdown (9.9). Nothing is recorded for a lost lease, whatever the handler returns. After the drain period, a result the handler still returns is recorded; an exception is not.
- The token is also cancelled when the attempt runs longer than the kind's `AttemptTimeout`. The handler then has 5 seconds to return. A result it returns in that time is recorded. If it throws, or has not returned, the attempt is recorded as a retry with the error "timed out", and its slot is freed whether or not the handler has returned. That retry is written in a new scope, because the handler may still be using its own `DbContext`. A handler that ignores the token keeps running outside the concurrency limit, keeps its scope until it ends, and its late result is discarded, so handlers must honour the token.
- `RetryAt` uses an attempt like any other retry. A time in the past makes the message due at once.
- `Done(record)` is for follow-up database writes. After the handler returns, the library opens a transaction on the scope's `DbContext`, writes the fenced completion first, then runs `record` with the attempt's token, saves and commits. If the completion matches no row, `record` never runs. If `record`, the save or the commit throws, the transaction rolls back and the attempt is recorded as a failed one with that exception. Under a retrying execution strategy `record` can run more than once, so it must only touch the database. Changes the handler left tracked in the scope's `DbContext` are saved with `record`'s.

**The give-up hook.** The library gives up on a message when it parks it (its attempts or its retry window are used up) or expires it (its deadline has passed). A handler never sees that happen: a failed last attempt is parked after the handler returned, and a message found spent or expired by the claim is never handed to a handler. A service whose record has a status of its own, such as a delivery that must become failed, needs to know. A kind has a give-up hook when the handler class registered for it also implements `IOutboxGiveUpHandler`. There is no option to set.

- The library calls `OnGiveUpAsync` on every path that parks or expires a message of that kind: a failed attempt that leaves nothing, a claim that finds the message spent after an attempt that never reported, and a claim that finds it past its deadline. It is not called for `Reject`, where the handler already knows, or for an operator's cancel or retry.
- The reason is `AttemptsUsedUp` or `RetryWindowPassed` when the message becomes `Parked`, and `DeadlinePassed` when it becomes `Expired`. When both the attempts and the window are used up, it is `AttemptsUsedUp`. `delivery.Attempt` is the number of attempts made, which is 0 for a message that expired before its first attempt.
- The hook runs on a new handler instance from a scope of its own, never the scope of the failed attempt, so changes a failed handler left tracked are not saved.
- The library opens a transaction on that scope's `DbContext`, writes the fenced status change first (9.8), then calls the hook, saves the `DbContext` and commits. The hook's writes are never stored without the status change. `IOutbox.AddAsync` works inside the hook, because a transaction is open.
- If the status change matches no row, the lease was lost: the hook never runs and nothing is written.
- If the hook, the save or the commit throws, the transaction rolls back, so none of the hook's writes are stored. The library then stores the status change on its own, fenced by the same token, with the error `The give-up hook failed:` followed by the exception, passed through `RedactError` and cut like every other error. It logs this at error level and counts it (9.11). So the message is never lost and never stays claimable, at the price that its status is stored without the hook's writes in this one case. Decision 15 in 9.14 has the reason. A failure that passes on its own, such as a dropped connection, is retried by the service's execution strategy when it has a retrying one, before the hook counts as failed.
- The hook's token is cancelled at the end of the drain period on shutdown. If the hook throws after that, nothing is recorded, and the message is held again after its lease.
- The hook can run more than once for one message: when a retrying execution strategy repeats the transaction, when a message was held and its end was never written, and once for each time a message is parked after an operator retry. So it must only touch the database and must be safe to repeat.
- Nothing runs after the commit. A service raises its alert from its own record, or queues a message for it inside the hook.

### 9.6 Registration

```csharp
services.AddDurableOutbox<AppDbContext>(outbox =>      // OutboxOptions; the callback is optional
{
    outbox.PollInterval = TimeSpan.FromSeconds(1);
    outbox.RedactError = error => error;          // the service's filter for stored and logged error text
    outbox.RunWorkers = true;                     // false in a process that only writes
    outbox.DrainTimeout = TimeSpan.FromSeconds(20);

    outbox.AddHandler<SendReceiptHandler>("receipt.send", kind =>   // OutboxKindOptions; optional
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

The values above are the defaults. Options are checked when the registration call runs: an invalid one (an empty or repeated kind, a lease, poll interval, backoff base, attempt timeout or retry window that is not positive, a backoff cap below the base, a concurrency or attempt limit below 1, a negative drain timeout, no error filter) throws `ArgumentException` naming the kind and the option. The library keeps a copy, so changing the options object afterwards has no effect.

Each handler type is registered as scoped, whatever `RunWorkers` says, so a test can run them on demand (9.12). The workers start with the host when `RunWorkers` is true and at least one handler is registered; otherwise the host claims nothing. The registration always provides the writer, the operator API (9.10) and the start-up check (9.11). A process claims only the kinds it has a handler for: messages of any other kind wait untouched.

The schema and table name are read from the EF Core model, so the SQL is built once and always matches the migration.

### 9.7 Claiming

One worker loop per kind. Each claim is a single statement, sent on its own with no surrounding transaction:

```sql
WITH due AS MATERIALIZED (
    SELECT id,
           CASE
               WHEN deadline_at <= @now THEN 'Expired'
               WHEN attempts >= @max_attempts
                 OR (attempts > 0 AND created_at <= @give_up_before) THEN 'Parked'   -- "spent"
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
       last_error      = CASE WHEN due.abandoned THEN @error ELSE m.last_error END,
       errors          = CASE WHEN due.abandoned THEN <errors with this attempt appended> ELSE m.errors END
  FROM due
 WHERE m.id = due.id
RETURNING m.id, m.record_ref, m.status, m.attempts, m.created_at, m.deadline_at, m.trace_parent, due.abandoned;
```

`@give_up_before` is now minus the kind's retry window, or null when it has none. `@error` here is the fixed text for an attempt that never reported. The appended error history is the expression in 9.8. `abandoned` is returned so the attempt that never reported can be counted (9.11).

The retry window applies only to a message that has been attempted. A message with no attempts, whether new or reset by an operator retry (9.10), is claimed rather than parked, so an operator retry always gets at least one attempt. Guarantee 4 is unaffected: an attempt is counted when it is claimed, so a crashed one has already been counted.

What the one statement does:

- **Claims** due messages for this kind, skipping rows another worker has locked.
- **Parks** a message that is due again with its attempts used up. That only happens when its last attempt never reported, which is how a crash loop ends (guarantee 4).
- **Expires** a message past its deadline. A message is checked when it becomes due, so one that is mid-attempt when its deadline passes is allowed to finish.
- **Records** an abandoned attempt in the error history.

`@limit` is the number of free concurrency slots. Rows returned as `Parked` or `Expired` are counted and logged, not handled.

A kind with a give-up hook (9.5) sends a second text of this statement instead. It is the same statement with the same `due` query, lock and order, and it differs only in what it writes to a row whose verdict is `Parked` or `Expired`. It does not end the row. It holds it: the row stays `Pending`, takes the claim's fresh token, and is due again at `@now + @lease`, with no attempt counted. The verdict is returned in place of the status. That is a claim like any other (9.2), so if the worker dies the row is due again after the lease and the next claim holds it again. The worker ends each held row in a transaction with the hook (9.8). A kind with no hook sends the statement above, unchanged.

```sql
UPDATE app.outbox_messages m
   SET attempts        = m.attempts + (due.verdict = 'Pending')::int,
       lease_token     = @token,
       next_attempt_at = @now + @lease,
       last_error      = CASE WHEN due.abandoned THEN @error ELSE m.last_error END,
       errors          = CASE WHEN due.abandoned THEN <errors with this attempt appended> ELSE m.errors END
  FROM due
 WHERE m.id = due.id
RETURNING m.id, m.record_ref, due.verdict AS status, m.attempts, m.created_at, m.deadline_at, m.trace_parent, due.abandoned;
```

### 9.8 Outcomes and renewal

Every outcome is one fenced statement, written with no cancellation token:

```sql
-- Done
UPDATE app.outbox_messages
   SET status = 'Completed', finished_at = @now, lease_token = NULL
 WHERE id = @id AND lease_token = @token AND status = 'Pending';

-- Failed (Retry or RetryAt). "spent" is the same test as in the claim:
-- attempts >= @max_attempts OR (attempts > 0 AND created_at <= @give_up_before)
UPDATE app.outbox_messages
   SET status          = CASE WHEN <spent> THEN 'Parked' ELSE 'Pending' END,
       finished_at     = CASE WHEN <spent> THEN @now END,
       next_attempt_at = CASE WHEN <spent> THEN next_attempt_at ELSE @next END,
       lease_token     = NULL,
       last_error      = @error,
       errors          = <errors with this attempt appended>
 WHERE id = @id AND lease_token = @token AND status = 'Pending'
RETURNING status;

-- Reject
UPDATE app.outbox_messages
   SET status = 'Rejected', finished_at = @now, lease_token = NULL,
       last_error = @error, errors = <errors with this attempt appended>
 WHERE id = @id AND lease_token = @token AND status = 'Pending';
```

A failed attempt chooses between retry and park inside the statement, from the row's own attempt count, so the choice cannot disagree with the row. One row affected means the outcome stands. None means the lease was lost: the result is discarded, logged and counted.

For a kind with a give-up hook (9.5), a failed attempt that leaves nothing is not parked by its statement. A second text of the statement records the error and makes the same choice from the row, but holds a spent row: it stays `Pending` under the attempt's own token for a full lease. The worker then ends it. A retry is still one statement with no transaction.

```sql
-- Failed, for a kind with a give-up hook
UPDATE app.outbox_messages
   SET next_attempt_at = CASE WHEN <spent> THEN @now + @lease ELSE @next END,
       lease_token     = CASE WHEN <spent> THEN lease_token END,
       last_error      = @error,
       errors          = <errors with this attempt appended>
 WHERE id = @id AND lease_token = @token AND status = 'Pending'
RETURNING CASE WHEN <spent> THEN 'Parked' ELSE 'Pending' END;

-- End of a held message. The only way a message of such a kind becomes Parked or Expired.
UPDATE app.outbox_messages
   SET status = 'Parked',                         -- or 'Expired'
       finished_at = @now, lease_token = NULL,
       last_error = COALESCE(@error, last_error),
       errors = CASE WHEN @error IS NULL THEN errors ELSE <errors with this entry appended> END
 WHERE id = @id AND lease_token = @token AND status = 'Pending';
```

The end is sent first in a transaction with the hook, with a null `@error`. When the hook fails, it is sent again on its own with the hook's error. Zero rows means the lease was lost, as for any outcome.

Every statement that records an error, the claim included, appends through the same expression, which keeps the most recent 20 entries:

```sql
(SELECT jsonb_agg(entry ORDER BY position)
   FROM (SELECT entry, position
           FROM jsonb_array_elements(
                    errors || jsonb_build_object('attempt', attempts, 'at', @now, 'error', @error))
                WITH ORDINALITY AS history(entry, position)
          ORDER BY position DESC
          LIMIT 20) AS recent)
```

The retry delay is `min(base × 2^(attempt − 1), cap)`, multiplied by a random factor between 0.8 and 1.2, counted from the time the failure is written. `RetryAt` gives the due time instead.

Renewal runs per kind every third of the lease and extends every attempt whose handler is still running, in one statement. Each claim has its own token, so the running attempts of a kind carry many tokens, and each message is fenced by the token of the claim that holds it. The statement takes the IDs and tokens as two arrays of pairs:

```sql
WITH held AS MATERIALIZED (
    SELECT m.id
      FROM app.outbox_messages m
      JOIN unnest(@ids, @tokens) AS h(id, token) ON m.id = h.id AND m.lease_token = h.token
     WHERE m.status = 'Pending'
       FOR UPDATE OF m SKIP LOCKED
)
UPDATE app.outbox_messages m
   SET next_attempt_at = @now + @lease
  FROM held
 WHERE m.id = held.id
RETURNING m.id;
```

The primary key serves it. `SKIP LOCKED` keeps a renewal from waiting on a row that another statement holds, such as a claimer taking the message over; a row skipped counts as not renewed. An attempt leaves the renewal set the moment its handler returns, before its outcome is written, so a renewal never meets its own completion.

An attempt has lost its lease, and its handler's token is cancelled, when:

- a renewal reached the database and did not return its ID, or
- its lease end (the claim time or the last successful renewal, plus the lease) has passed without a renewal that reached the database. This stops a handler during an outage instead of leaving it to send while another instance may take the message over.

Nothing is recorded for an attempt that lost its lease.

### 9.9 Worker loop

For each kind:

1. Wait until at least one concurrency slot is free.
2. Claim up to that many messages.
3. Start each on its own task and scope. On finish, write the outcome with no cancellation token and free the slot.
4. If the claim returned as many messages as it asked for, go again as soon as a slot frees. Otherwise wait `PollInterval`, multiplied by a random factor between 0.8 and 1.2. Messages the claim parked or expired count towards what it returned; they are logged, never handled, and use no slot. For a kind with a give-up hook the claim holds them instead (9.7): each is ended on its own task, uses a slot until its end is written, and is logged then. It is not renewed, because once its end statement has run the row's lock keeps other claims away.
5. If a claim fails because the database is unreachable or returns an error, log it and wait: `PollInterval` after the first failure, doubling after each one in a row, up to 30 seconds. The first claim that succeeds returns the loop to normal polling. Nothing is lost: claims expire on their own.

The loop also runs the renewal (9.8). Claim and renewal are statements of the loop itself, one at a time, each in a short scope of its own, so a kind uses one connection for them besides its handlers. An outcome write that fails is logged and not retried; the message comes back after its lease as an abandoned attempt.

Because each kind has its own loop and its own limit, a slow kind cannot hold up another. The workers run as one hosted service per `DbContext` type, with one loop per registered kind.

On shutdown the loop stops claiming at once and keeps renewing. It waits up to `DrainTimeout` (default 20 seconds, inside the host's 30) for running handlers, then cancels their tokens and keeps waiting for them and their outcomes until the host's own shutdown time ends. An outcome a handler returned is always recorded. Anything still unfinished is recovered by lease expiry.

The loop writes source-generated log messages when the database cannot be reached, when a lease is lost, when a handler throws, when an attempt times out, when the claim parks or expires a message, when a give-up hook fails, and when an outcome is refused or cannot be written. Each carries the message ID, kind and attempt where they exist. Error text in them has passed through `RedactError`.

All times come from `TimeProvider`. A lease must be much longer than the clock difference between instances. A clock difference can cause an early takeover and so a repeated send, which at-least-once already allows; the token still prevents a wrong write.

### 9.10 Operator API

```csharp
public interface IOutboxAdmin
{
    Task<IReadOnlyList<OutboxMessageInfo>> ListAsync(OutboxQuery query, CancellationToken cancellationToken);
    Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken);   // Parked or Rejected → Pending, attempts reset, errors kept
    Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken);  // Pending or Parked → Cancelled
}

public enum OutboxMessageStatus { Pending, Completed, Rejected, Parked, Expired, Cancelled }

public sealed record OutboxQuery(OutboxMessageStatus Status)   // Parked or Rejected
{
    public string? Kind { get; init; }
    public int Limit { get; init; } = 50;                      // 1 to 1,000
    public OutboxMessageInfo? After { get; init; }             // the last entry of the previous page
}

public sealed record OutboxMessageInfo(
    Guid Id, string Kind, string RecordRef, string? UniqueKey, int Priority,
    OutboxMessageStatus Status, int Attempts, DateTimeOffset CreatedAt, DateTimeOffset? Deadline,
    DateTimeOffset? FinishedAt, string? LastError, IReadOnlyList<OutboxAttemptError> Errors);

public sealed record OutboxAttemptError(int Attempt, DateTimeOffset At, string Error);
```

`IOutboxAdmin` is registered as scoped by `AddDurableOutbox<TContext>`, on the same `DbContext`.

`ListAsync` lists the messages an operator acts on: `Parked` or `Rejected`, optionally of one kind, newest finished first. It pages by `(finished_at, id)`: pass the last entry of one page as `After` to get the next, and no message is skipped or repeated. Any other status, a limit outside 1 to 1,000, or an `After` with no finish time throws `ArgumentException`. The status is written into the SQL text, not passed as a parameter, so PostgreSQL can use the partial index for it:

```sql
SELECT id, kind, record_ref, unique_key, priority, status, attempts,
       created_at, deadline_at, finished_at, last_error, errors
  FROM app.outbox_messages
 WHERE status = 'Parked'                                   -- or 'Rejected'
   [AND kind = @kind]
   [AND (finished_at, id) < (@after_finished_at, @after_id)]
 ORDER BY finished_at DESC, id DESC
 LIMIT @limit;
```

With a kind, the operator index is read in order. Without one, every parked (or rejected) entry is read and sorted, which is cheap while that set is small.

Retry and cancel are one statement each, by primary key. Each returns whether the message was in a status it applies to:

```sql
-- Retry
UPDATE app.outbox_messages
   SET status = 'Pending', attempts = 0, next_attempt_at = @now, lease_token = NULL, finished_at = NULL
 WHERE id = @id AND status IN ('Parked', 'Rejected');

-- Cancel
UPDATE app.outbox_messages
   SET status = 'Cancelled', finished_at = @now, lease_token = NULL
 WHERE id = @id AND status IN ('Pending', 'Parked');
```

A retried message is due at once, its attempt limit starts again, and its error history is kept. A retried message whose deadline has passed is expired by the next claim.

Cancelling a message that is mid-attempt clears its token, so that attempt's outcome is refused (guarantee 3). The next renewal finds no row under the attempt's token, so the worker treats the lease as lost and cancels the handler's token, at most a third of the lease later. The library adds no endpoints; the service decides who may call these.

### 9.11 Telemetry and health

Meter `DurableOutbox`:

| Instrument | Type | Tags |
| :--- | :--- | :--- |
| `durable_outbox.messages.enqueued` | Counter | `kind` |
| `durable_outbox.attempts` | Counter | `kind`, `outcome` (`completed`, `retry`, `timed_out`, `rejected`, `parked`, `lease_lost`, `abandoned`) |
| `durable_outbox.messages.expired` | Counter | `kind` |
| `durable_outbox.give_up_hook.failures` | Counter | `kind` |
| `durable_outbox.attempt.duration` | Histogram, seconds | `kind`, `outcome` |
| `durable_outbox.messages.pending` | Gauge | `kind` |
| `durable_outbox.messages.parked` | Gauge | `kind` |
| `durable_outbox.oldest_due.age` | Gauge, seconds | `kind` |

The meter comes from the container's `IMeterFactory` when it has one, as a generic host does; otherwise the library creates its own.

`messages.enqueued` counts each message `AddAsync` inserts, when it inserts it. A duplicate is not counted, but a message whose transaction later rolls back is: counting at commit would need a hook into the caller's transaction. `messages.expired` counts each message whose expiry is stored: by the claim, or for a kind with a give-up hook by the end written after the claim held it. `give_up_hook.failures` counts each give-up hook that failed and left its message ended without it (9.5).

`attempts` is counted by the worker that ran the attempt, once, by how it ended: `completed` when its completion stood; `retry` when its failure was recorded and the message is due again; `timed_out` when it ran past its timeout and was recorded as a retry that left attempts; `rejected` when its rejection stood; `parked` when its failure left no attempts and, for a kind with a give-up hook, the end that followed stood, with or without the hook; `lease_lost` when its lease was lost or its outcome refused, the end of a held message included. An attempt left with no outcome for another reason (the outcome could not be written, or the handler threw after the drain period) is not counted by its worker. A claim that finds an attempt that never reported counts it `abandoned`. So an attempt whose lease ran out while its worker was alive is counted twice: `lease_lost` by its worker and `abandoned` by the claim that took the message over. `attempt.duration` is recorded with each attempt the worker counts, from the start of the handler to the outcome being written, or to the handler's return when nothing was recorded.

The three gauges come from one statement, run every 15 seconds in each process that runs workers or registers the health check:

```sql
SELECT kind, count(*) AS pending, 0::bigint AS parked,
       min(next_attempt_at) FILTER (WHERE next_attempt_at <= @now) AS oldest_due
  FROM app.outbox_messages WHERE status = 'Pending' GROUP BY kind
UNION ALL
SELECT kind, 0, count(*), NULL
  FROM app.outbox_messages WHERE status = 'Parked' GROUP BY kind;
```

Each half is served by one partial index. Every kind in the table is reported, whether or not the process has a handler for it. `oldest_due.age` is the time since the earliest due pending message became due, or zero when none is due; a message under a live claim is not due, because its due time is its lease end. Reading a gauge returns the last sample and never queries. The interval is fixed. A process with two outbox tables that share a kind reports two series with the same tags.

Tracing uses the activity source `DurableOutbox`. `AddAsync` stores the current trace context on the row. Each attempt starts a consumer activity `outbox.handle {kind}` with that context as its parent, or a new trace when there is none, so a trace runs from the request to the delivery. The activity is current while the handler runs, so the handler's own calls join the trace. Its tags follow the OpenTelemetry messaging conventions where they fit: `messaging.system` (`durable_outbox`), `messaging.operation.type` (`process`), `messaging.destination.name` (the kind) and `messaging.message.id`, plus `durable_outbox.attempt` and `durable_outbox.outcome`. It is marked as an error when the attempt did not complete.

Logs use source-generated messages with the message ID, kind and attempt, under the category `DurableOutbox`. They arrive with the workers (9.9). The record reference is logged; error text is logged only after the service's filter.

The health check is registered on the service's health checks:

```csharp
services.AddHealthChecks().AddDurableOutbox<AppDbContext>(degradedAfter: TimeSpan.FromMinutes(5));
```

It reports `Degraded`, naming the kinds, when the last sample shows a kind whose oldest due message has waited longer than `degradedAfter`, and `Degraded` when the last sample could not be taken. Otherwise, and before the first sample, it reports `Healthy`. It reads the sample and runs no query of its own. It never reports `Unhealthy`: a backlog is not fixed by restarting the process.

When the host starts, the library compares the table's columns and indexes with what it expects, in a hosted service that every `AddDurableOutbox` call registers. It reads the database catalog only. Every column of 9.3 must exist with its type and nullability, and each index of 9.3 must exist with the same columns, direction, uniqueness and filter, matched by shape rather than by name. Extra columns and indexes are allowed. On a mismatch it logs every difference and fails with an `InvalidOperationException` listing them, which stops the host under the default `BackgroundServiceExceptionBehavior.StopHost`. When the database cannot be reached, it logs that and tries again with the same backoff as the worker loop (9.9). The workers claim nothing until the check has passed.

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
| Other | Priority order, deadline expiry, backoff timing, `Done(record)` committing with the completion and rolling back when the lease is lost, drain on shutdown, recovery after the database restarts. The give-up hook on each path that parks or expires, with its writes committed with the status; refused when the lease was lost; and a hook that throws. |

`DurableOutbox.Testing` publishes the guarantee tests as methods a service calls against its own `DbContext` and schema, with no dependency on a test framework. It is published with every release, at the library's version (section 6, decision 12), and the library's own tests use it too:

```csharp
namespace DurableOutbox.Testing;

public sealed class OutboxContract<TContext> where TContext : DbContext
{
    // addDbContext registers TContext against a database whose outbox table the service's migrations created.
    public OutboxContract(Action<IServiceCollection> addDbContext);

    public Task WrittenWithTheChangeAsync(CancellationToken cancellationToken = default);   // guarantee 1
    public Task AtLeastOnceAsync(CancellationToken cancellationToken = default);            // guarantee 2
    public Task OneOwnerAtATimeAsync(CancellationToken cancellationToken = default);        // guarantee 3
    public Task CrashIsAnAttemptAsync(CancellationToken cancellationToken = default);       // guarantee 4
    public Task NeverDroppedAsync(CancellationToken cancellationToken = default);           // guarantee 5
    public Task GiveUpHookAsync(CancellationToken cancellationToken = default);             // the give-up hook, 9.5
}
```

Each method builds its own service provider with the service's `DbContext`, a fake clock it moves itself, and handlers for kinds it generates for that call, so it never waits on real time and never touches the service's own messages. A method completes when its guarantee holds and throws `InvalidOperationException`, naming the guarantee and what differed, when it does not. The rows it wrote stay in the table, so the kit should be pointed at a database used only for tests. Guarantee 6 is a promise not made, so it has no method. `GiveUpHookAsync` is not a guarantee of 5.3 but runs under the same rules, because the hook depends on statements of its own: with a kind of its own whose hook queues a message, it checks that a message parked by a failed last attempt, one parked at the claim and one expired at the claim each commit that write with the status, and that a hook that throws leaves the message parked with the hook's error and without the write. The library's own tests prove guarantees 1 to 5 by calling these methods against two `DbContext` types with different schemas and table names, so there is one copy of each test.

The kit also has `RunUntilIdleAsync`, for a service's integration tests:

```csharp
public static Task RunUntilIdleAsync<TContext>(this IServiceProvider services, CancellationToken cancellationToken = default)
    where TContext : DbContext;
```

It runs the handlers registered for `TContext`, with their options, until no message of a registered kind is due and no attempt is running, then returns with every outcome written. That includes a message that a handler or a give-up hook of one kind adds for another kind: each kind's loop stops by itself, so the loops are run again until a pass leaves no pending message of a registered kind due (since 1.2.0). It works with `RunWorkers` set to false, which is how a test host usually registers the outbox: `AddDurableOutbox` registers the handlers whatever `RunWorkers` says. It uses the service's `TimeProvider` and does not move it, so a message whose next attempt is in the future is left for later. It throws `InvalidOperationException` when no handler is registered for `TContext`, or when the start-up check fails. A claim that fails ends the run and throws the claim's error (since 1.2.0): the hosted worker waits and claims again, but here the clock is the test's and may never move, so the wait would not end. The hosted worker is unchanged.

### 9.13 Operating notes and known limits

- **Delivery delay.** A new message waits up to 1.2 times `PollInterval` (1 second by default): the poll with its jitter.
- **Repeated sends.** If renewal fails for longer than the lease, for example while the database is unreachable, another worker can take the message. The first handler's token is cancelled when its lease end passes, but a handler that does not stop promptly can still be sending.
- **Table growth.** Finished rows stay until the service removes them. They are not in the claim index, so they do not slow claiming, but the service needs a retention job or partitioning.
- **Idle claims.** PostgreSQL walks the claim index in priority order, so a claim that finds nothing due reads every index entry for its kind: the messages that are leased or waiting for a retry. Measured on PostgreSQL 17 with 100,000 such messages and none due, that took 0.16 ms with 3 distinct priorities and 2.2 ms with about 2,000. Finished messages are not in the index and cost nothing. A kind with millions of waiting messages will pay this on every poll.
- **Vacuum.** Each message is updated at least twice, and those updates touch an indexed column. On a busy table, autovacuum should be set to run more often than the default.
- **Connections.** Each kind uses up to `MaxConcurrency` connections for handlers, plus one for claiming and renewal. All kinds share the service's connection pool, so the pool must cover the sum of every kind's `MaxConcurrency`, plus one per kind, plus what the service's own requests need. A pool smaller than that lets one busy kind starve the others and the service itself.
- **Slow work within a kind.** Kinds are isolated from each other, but messages of one kind share its slots. If a kind sends to several destinations and one is slow, that destination can hold every slot. Register a separate kind for work that must not be held up.
- **Gauge sampling.** Every process that runs workers or registers the health check reads every pending and parked index entry of the table every 15 seconds (9.11). It is the same set of entries an idle claim reads, at a much lower rate, but a table with millions of waiting messages pays it in each such process.
- **Counting inserts.** `durable_outbox.messages.enqueued` counts a message when `AddAsync` inserts it, so a transaction that rolls back afterwards still counts it.
- **Cancel latency.** An operator cancel refuses the running attempt's outcome at once, but its handler is cancelled only at the next renewal, up to a third of the lease later.
- **A held message that is never ended.** For a kind with a give-up hook, a message is held under a lease before it is ended (9.7, 9.8). If the worker dies, or the database fails, between the hold and the end, the next claim finds the message under a token and records it as an attempt that never reported: one more error entry and one more `abandoned` count, for a message whose attempt may have reported. There is no column to tell the two apart. The message is then held again and ended as usual.
- **A give-up hook that fails.** The message is parked or expired without the hook's writes (9.5), so the service's record is not marked. The error log and `durable_outbox.give_up_hook.failures` say so. A parked message shows the hook's error in the operator list, and an operator retry gives it a new attempt and a new call to the hook. An expired message cannot be retried, so for those the service corrects its record from the log or the row.
- **A slow give-up hook.** A held message uses one of its kind's slots until its end is written, so a slow hook holds up the kind's handlers as a slow handler would.
- **Retry window after an operator retry.** An operator retry resets the attempts but not the creation time, so a message of a kind with a retry window that has passed gets exactly one attempt per retry (9.7).

### 9.14 Design decisions made

| # | Decision | Reason |
| :--- | :--- | :--- |
| 1 | `AddAsync` needs an explicit transaction and inserts at once. The implicit transaction of a lone `SaveChanges` is not enough. | It is what makes "duplicate ignored" and "refused outside a transaction" possible. |
| 2 | Column names are fixed by the library. Only the schema and table name are the service's choice. | It keeps the SQL constant and the start-up check simple. |
| 3 | An attempt's trace is a child of the writer's trace, including retries that run much later. | A link would be more exact for late retries but makes the common case harder to follow. |
| 4 | Defaults: poll 1 second, lease 60 seconds, 8 concurrent attempts per kind, attempt timeout 5 minutes, 10 attempts, backoff 5 seconds to 15 minutes, drain 20 seconds. | Starting values, all configurable. |
| 5 | Renewal fences each message on the token of the claim that holds it, sent as pairs in one statement. | One token per claim is what refuses a late result when the same worker claims a message again. One statement per renewal keeps queries out of loops. |
| 6 | A handler's token is also cancelled when its lease end passes without a renewal that reached the database. | During an outage another instance can take the message. Stopping the first handler costs one attempt, which is cheaper than a repeated send. |
| 7 | Options are checked when the registration call runs, and copied. | It fails in the line that is wrong, before the host starts, and a later change to the options object cannot change a running worker. |
| 8 | The workers' log messages arrive with the workers, not with the rest of the telemetry. | The loop's failures (an unreachable database, a lost lease) would otherwise be silent until the metrics exist. |
| 9 | The retry window applies only to a message that has been attempted. | An operator retry resets the attempts but not the creation time; without this, a message parked by its window would be parked again at once, with no attempt. |
| 10 | The operator statements write the status into the SQL text and accept only `Parked` and `Rejected` for lists. | A status passed as a parameter cannot be matched to a partial index, and the other statuses have no index to page by finish time on. |
| 11 | The gauges are sampled every 15 seconds by one statement and read from memory. | A query inside the gauge callback would let the metrics collector decide the database load, and the health check would query on every probe. |
| 12 | The start-up check gates the workers and retries an unreachable database instead of failing the start. | A table that does not match is a deployment error and must stop the host; a database that is briefly down is not, and the loop already survives it. |
| 13 | The test kit depends on the exact same version of the library and on `Microsoft.Extensions.TimeProvider.Testing`. In the repository that is a project reference; in the package it is the dependency `[version]`, not NuGet's usual `>= version`. | It runs the library's own claim and worker internals against the service's schema, and moves a fake clock as the library's tests do. |
| 14 | For a kind with a give-up hook, the claim and the failed-attempt statement hold a spent or expired message under a lease instead of ending it, and the worker ends it in a transaction with the hook. A kind with no hook keeps its statements unchanged. | The claim is one statement with no surrounding transaction, so it cannot run the service's code with the status change. A hold is a claim like any other, so nothing new has to recover it. Running the existing failed-attempt statement inside a transaction would put a transaction around every failure of a kind with a retry window. |
| 15 | A give-up hook that throws does not stop the message ending: it is parked or expired without the hook's writes, with the hook's error on the row, in the log and in a counter. | Calling the hook again until it works leaves a message with a broken hook claimable for ever and pending past its limit. Bounding the calls needs a counter, and there is no column for one without changing every service's table. Failures that pass on their own are the execution strategy's to retry. |
| 16 | A held message uses one of its kind's slots until its end is written. | A full claim is followed by another at once, so hooks with no slot could pile up without limit and use the service's connection pool. |
| 17 | `Reject` takes no record to commit with the rejection. | The handler decides to reject while it runs, so it can write its record before it returns, and a repeat of that is harmless. No caller needs the overload. It can be added later without breaking anything. |
| 18 | The give-up hook is an interface on the kind's handler class, not an option. | The hook needs the scope's `DbContext`, which a handler class already gets by injection, and it keeps a kind's logic in one class. |
| 19 | The types the library reads query results into name every result column with `[Column]`. | A naming convention on the service's `DbContext` applies to these types too and would look for other names, so no message would ever be claimed. An attribute outranks a convention. |
| 20 | In `RunUntilIdleAsync` a failed claim ends the run with its error, and the loops are run again until nothing is due. | A test's clock may never move, so the worker's wait before the next claim would hang the test and hide the error. Kinds stop one by one, so work one kind adds for another is only found by another pass. |

### 9.15 What is verified

Built and tested against PostgreSQL 17 (change `add-outbox-store`):

- The table and its two indexes (9.3), created by a migration generated from the library's configuration.
- `AddAsync` (9.4), including refusal outside a transaction and duplicate uniqueness keys.
- The claim (9.7): four concurrent claimers take 1,000 messages exactly once. With the row lock removed from the statement, the same test hands out 1,300.
- Recovery and parking of attempts that never report, parking by retry window, and expiry by deadline.
- The done, failed and rejected outcomes (9.8), refused under a stale token, and the 20-entry error history.

Built and tested against PostgreSQL 17 (change `add-outbox-workers`), with the real workers driven by a fake clock:

- Handlers, results and registration (9.5, 9.6), with invalid options refused.
- The worker loop (9.9): a message attempted until done with the same ID, the concurrency limit, isolation between kinds, a message expired by the claim never handled, recovery after the database refused connections, and both outcomes of the drain on shutdown.
- Lease renewal (9.8): a renewed lease is not taken over across two claims, and a handler whose lease is lost, or whose lease ran out during an outage, is cancelled with nothing recorded. With renewal removed from the loop, three of these tests fail.
- The attempt timeout, including a handler that ignores its token; the backoff range; `RetryAt`; `Done(record)` in all three outcomes; and error text redacted and cut before it is stored or logged.

Built and tested against PostgreSQL 17 (change `add-operations-and-release`):

- The operator index (9.3), and the retry window applied only after an attempt (9.7): a message older than its window with no attempts is claimed as attempt 1; with the old rule the same test fails.
- The operator API (9.10): listing by status and kind, newest first, and paging over messages finished in the same instant; retry and cancel only from the statuses they apply to; and a cancel during an attempt, which cancels the handler at the next renewal and leaves the message `Cancelled`.
- Telemetry (9.11): the counters and the duration by outcome, including `abandoned` and `expired`; the gauges from one sample, with a message under a live claim not counted as due; the consumer activity, current inside the handler, with the writer's activity as its parent; and the health check, `Degraded` past its limit and when a sample fails.
- The start-up check (9.11): a missing column or index is reported by name, an extra column of the service's is allowed, and nothing is claimed; a database unreachable at start-up delays the check without stopping it.
- The test kit (9.12): guarantees 1 to 5 run as the kit's methods against `svc.outbox` and against a second context mapping `contract.messages`, beside a message of the service's own that they leave unchanged. With the claim's `FOR UPDATE SKIP LOCKED` removed, and separately with `lease_token = @token` removed from the completion, guarantee 3 fails for both contexts. `RunUntilIdleAsync` runs handlers with `RunWorkers` false and returns with a retried message left for later.
- Both packages packed for 1.0.0 with the MIT licence, the readme and symbols, and the kit depended on exactly the same version of the library. `dotnet pack` produces both again from 1.1.0 (section 6, decision 12).

Built and tested against PostgreSQL 17 (change `add-give-up-hook`, version 1.1.0):

- The give-up hook (9.5), by the test kit's `GiveUpHookAsync` against `svc.outbox` and `contract.messages`: a message parked by a failed last attempt, one parked at the claim after attempts that never reported, and one expired at the claim each store the hook's write with the status and get the right reason; a hook that throws leaves the message parked with the hook's error, without the write, and not claimable a day later. With the holding claim replaced by the plain one, the method fails for the message parked at the claim.
- A held message cancelled by an operator before its end is not ended; the hook does not run when the last attempt's lease was taken over; the reason is `RetryWindowPassed` when the window has passed with attempts left, and changes the hook left tracked are saved; with two slots and five expired messages no more than two hooks run at once; a hook that throws is counted, logged, and its attempt counted `parked`.
- `dotnet pack` of 1.1.0 passes package validation against the 1.0.0 baseline, for both packages. The kit's 1.1.0 package depends on `DurableOutbox` `[1.1.0]`.

Built and tested against PostgreSQL 17 (change `fix-naming-convention-and-idle-run`, version 1.2.0):

- A naming convention (9.3): guarantees 1 to 5 and the give-up hook run as the kit's methods against a third context registered with `UseSnakeCaseNamingConvention()`, and the start-up check, the gauges and the operator list read their rows under it. With the `[Column]` attributes removed, guarantees 2 to 5, the give-up hook and that test fail.
- `RunUntilIdleAsync` (9.12): with every claim made to fail by a trigger, on a table that passes the start-up check, the call throws the claim's error and the message is left pending with no attempt; with the old loop the test never ends. Two kinds whose give-up hooks each add a message for the other are run to the end of a chain of six messages in one call; with a single pass the test fails.
- `dotnet pack` of 1.2.0 passes package validation against the 1.0.0 baseline, for both packages. The kit's 1.2.0 package depends on `DurableOutbox` `[1.2.0]`.

Not yet verified: nothing has been run on PostgreSQL 14 to 16, and the GitHub Actions workflows have not run, because the repository has not been pushed. Their steps were run locally.

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
