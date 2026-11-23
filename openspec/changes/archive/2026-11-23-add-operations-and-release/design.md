# Design

## Context

Changes 1 and 2 left a working library: `OutboxStore<TContext>` with the claim, the fenced outcomes and renewal; `KindLoop<TContext>` running one loop per kind inside `OutboxWorker<TContext>`; source-generated logs in `OutboxLog`; and a registration that adds the worker only when `RunWorkers` is true and a handler exists, capturing the checked `OutboxSettings` in the worker's factory. The tests run the real worker on a `FakeTimeProvider`, stepping the clock only while every loop reports `Idle`. See `proposal.md` for the motivation and the seven spec deltas for the behaviour.

Constraints from `AGENTS.md` that shape the approach: the public API is section 9 and changes there first; every statement is raw SQL with parameters, and a status name is written in the text so a partial index is used; time comes from `TimeProvider`; no package beyond section 9.1 without asking (the owner approved `Microsoft.Extensions.TimeProvider.Testing` for the kit); `DurableOutbox.Testing` depends on no test framework; nothing is published or tagged without approval.

## Goals / Non-Goals

**Goals:**

- Every new statement served by an index in section 9.3, built once per table like the existing ones.
- Telemetry added next to the existing log calls in `KindLoop`, without reshaping the loop.
- One copy of each guarantee test, in the kit, run by the library's own tests against two schemas.
- Two packages that `dotnet pack` builds with licence, readme, symbols and source link, and a pipeline that proves every push.

**Non-Goals:**

- An admin screen or endpoints (section 5.6).
- Publishing to nuget.org. The publish workflow is written and reviewed here; running it is a separate, approved step.
- A public API baseline from an earlier package. There is none yet; package validation starts with the first release.
- Running the pipeline. The repository has no remote; the workflows are first run when the owner pushes it.

## Decisions

### 1. Operator statements, with the status written in the text

`OutboxAdmin<TContext> : IOutboxAdmin` is internal and scoped, built on the scope's `TContext` and the same `OutboxSqlProvider`. `OutboxSql` gains:

```sql
-- Retry: Parked or Rejected → Pending, attempts reset, errors kept
UPDATE <table>
   SET status = 'Pending', attempts = 0, next_attempt_at = @now, lease_token = NULL, finished_at = NULL
 WHERE id = @id AND status IN ('Parked', 'Rejected');

-- Cancel: Pending or Parked → Cancelled. Clearing the token fences out a running attempt.
UPDATE <table>
   SET status = 'Cancelled', finished_at = @now, lease_token = NULL
 WHERE id = @id AND status IN ('Pending', 'Parked');
```

Both use the primary key. The list is built from fixed fragments, never from values:

```sql
SELECT id, kind, record_ref, unique_key, priority, status, attempts,
       created_at, deadline_at, finished_at, last_error, errors
  FROM <table>
 WHERE status = 'Parked'                                   -- or 'Rejected', chosen from the enum
   [AND kind = @kind]
   [AND (finished_at, id) < (@after_finished_at, @after_id)]
 ORDER BY finished_at DESC, id DESC
 LIMIT @limit;
```

The status is written as text so that PostgreSQL can prove `status = 'Parked'` implies the operator index's filter; a parameter would not. With a kind, the operator index is read backwards in order. Without one, every parked (or rejected) entry is read and sorted, which is cheap while that set is small.

The public types:

```csharp
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

`Errors` is read from the `errors` column with `System.Text.Json`. Another status, a limit out of range, or an `After` with no finish time throws `ArgumentException`. Newest first, because an operator looks at what just broke.

*Alternatives:* a status parameter (loses the partial index); listing any status (`Pending` and `Completed` have no index to page `finished_at` on); a continuation token (opaque to the operator, and `After` is already what they hold).

### 2. Cancelling mid-attempt needs no change to the loop

A cancelled row has no token, so its renewal returns no ID and the loop already treats a missing ID as a lost lease: it cancels the handler's token and records nothing. The outcome, if the handler returns first, matches no row and is refused. The handler is stopped at the next renewal, a third of the lease at most.

### 3. The retry window applies only after an attempt

`OutboxSql.Spent` becomes `attempts >= @max_attempts OR (attempts > 0 AND created_at <= @give_up_before)`. The claim reads the attempts before it adds one, so a message with none, whether new or reset by an operator, is claimed rather than parked. The failed outcome always runs after a claim, so for it the change makes no difference. Guarantee 4 holds: a crashed attempt was counted when it was claimed. Sections 9.7 and 9.8 are corrected.

### 4. The operator index and its migration

`AddDurableOutbox(schema, table)` adds `HasIndex(Kind, FinishedAt).HasFilter("status IN ('Parked', 'Rejected')").HasDatabaseName($"ix_{table}_finished")`. The test project adds a migration for it. Services add one migration when they upgrade, which the README says.

### 5. Metrics are recorded where the loop already logs

An internal singleton `OutboxMetrics` owns the meter and its instruments. It takes the `Meter` from `IMeterFactory` when the container has one (a generic host registers it), and otherwise creates `new Meter("DurableOutbox")`, which it disposes with the container. Kind tags are passed as `TagList`. Where each counter is recorded:

| Instrument | Where |
| :--- | :--- |
| `messages.enqueued` | `Outbox.AddAsync`, when the insert returned one row. A transaction that later rolls back has still counted it (section 9.13). |
| `messages.expired` | `KindLoop.ClaimAsync`, for each row returned `Expired`. |
| `attempts{abandoned}` | `KindLoop.ClaimAsync`, for each row the claim found abandoned. The claim's `RETURNING` gains `due.abandoned`. |
| `attempts`, `attempt.duration` | `KindLoop`, once per attempt, from the outcome written: `CompleteAsync` true → `completed`; `FailAsync` `Retried` → `retry` (or `timed_out` when the error is the timeout), `Parked` → `parked`; `RejectAsync` true → `rejected`; any refused write, or a lost lease → `lease_lost`. Duration is measured with `TimeProvider.GetTimestamp()`. |

A failed outcome write and a handler that throws after the drain record nothing; the next claim counts them `abandoned`. That gives the double count the spec states for a lease that ran out while its worker was alive. *Alternative:* count only from the claim side. Rejected: a lost lease is the worker's event, and an operator needs both.

The tests listen with `MeterListener` and keep only instruments whose `Meter` is the test's own `OutboxMetrics` instance, so tests running in parallel do not see each other's measurements. No new test package.

### 6. The gauges are sampled by one statement, read from memory

An internal `OutboxSampler<TContext>` is a `BackgroundService` that runs, every 15 seconds on `TimeProvider`, starting at once:

```sql
SELECT kind, count(*) AS pending, 0::bigint AS parked,
       min(next_attempt_at) FILTER (WHERE next_attempt_at <= @now) AS oldest_due
  FROM <table> WHERE status = 'Pending' GROUP BY kind
UNION ALL
SELECT kind, 0, count(*), NULL
  FROM <table> WHERE status = 'Parked' GROUP BY kind;
```

Each arm is served by one partial index. The result is folded per kind into an immutable snapshot with the time it was taken, or a failed snapshot when the query threw (logged as the database being unreachable). `OutboxMetrics` registers the three observable gauges once; their callbacks read the latest snapshot of every registered sampler and never query. A live claim's `next_attempt_at` is its lease end, so it is not counted as due.

The sampler is registered with `AddHostedService` when the workers are, and by the health check registration; `AddHostedService` adds a type only once. The interval is fixed: section 9.11's "by default" is removed, since no caller sets it.

*Alternatives:* a query inside the gauge callback (the collector's schedule would decide the database load, and the health check would query); one statement with `status IN (…)` (cannot use either partial index).

### 7. The health check reads the sampler

```csharp
public static IHealthChecksBuilder AddDurableOutbox<TContext>(this IHealthChecksBuilder builder, TimeSpan degradedAfter)
    where TContext : DbContext;
```

It registers `OutboxHealthCheck<TContext>` under the name `DurableOutbox ({TContext name})` with failure status `Degraded`, and the sampler. The check returns `Degraded` naming the kinds over the limit, or `Degraded` when the last snapshot failed, otherwise `Healthy` (before the first snapshot too). `degradedAfter` must be positive. The package reference `Microsoft.Extensions.Diagnostics.HealthChecks` is in section 9.1.

### 8. One activity per attempt, on a static source

`ActivitySource("DurableOutbox")` is static, as activity sources are. The attempt's task starts `outbox.handle {kind}` with `ActivityKind.Consumer` and the parent parsed from `trace_parent` with `ActivityContext.TryParse`, before the handler is resolved, so the handler runs inside it. `RunAttemptAsync` keeps the `Activity` and, once the outcome is known, sets the outcome tag, an error status when the attempt did not complete, and disposes it. Tags follow the OpenTelemetry messaging conventions where they fit: `messaging.system = durable_outbox`, `messaging.operation.type = process`, `messaging.destination.name` = kind, `messaging.message.id`, and `durable_outbox.attempt`, `durable_outbox.outcome`. The test listens with an `ActivityListener` and finds its activity by message ID.

### 9. The start-up check is a hosted service that gates the workers

An internal `OutboxSchemaCheck<TContext>` is a `BackgroundService` registered by every `AddDurableOutbox` call. It reads the catalog in one statement:

```sql
SELECT 'column' AS kind, a.attname AS name, format_type(a.atttypid, a.atttypmod) AS detail, a.attnotnull AS flag
  FROM pg_attribute a
 WHERE a.attrelid = to_regclass(@table) AND a.attnum > 0 AND NOT a.attisdropped
UNION ALL
SELECT 'index', c.relname, pg_get_indexdef(i.indexrelid), i.indisunique
  FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
 WHERE i.indrelid = to_regclass(@table);
```

The expected shape lives in one internal class beside the configuration: the fifteen columns of section 9.3 with the type `format_type` prints and their nullability, and the primary key and three indexes as columns with direction, uniqueness and filter, written as PostgreSQL prints them. The columns and filter are taken from `pg_get_indexdef`'s text. Indexes are matched by shape, not name. Both sides are compared after removing whitespace, parentheses, double quotes and `::text` casts. No rows means the table is missing.

The check signals an internal `OutboxSchemaGate<TContext>` singleton (a `TaskCompletionSource`). Each `KindLoop` awaits the gate, with the stopping token, before its first claim. On a mismatch the check logs every difference, faults the gate so the loops end without claiming, and throws `InvalidOperationException` from `ExecuteAsync` with the same list; the host's default `BackgroundServiceExceptionBehavior.StopHost` then stops it. When the catalog query throws, the check logs the outage and tries again after `Backoff.Outage`, as the loop does. The tests assert on the check's `ExecuteTask` and on the rows, since they start hosted services without a host.

*Alternatives:* `StartAsync` that throws (an unreachable database at start-up would stop the host, which the owner declined); a check on first use (a writer-only process would fail its first request instead of its start); comparing names (a service that renames an index in its migration would fail for no reason).

### 10. Registration always keeps the settings and the handlers

`AddDurableOutbox<TContext>` registers an internal `OutboxRegistration<TContext>` singleton holding the checked settings, and each handler type as scoped, whatever `RunWorkers` says. The worker and the sampler are hosted only when `RunWorkers` is true and a handler exists, as before. `IOutboxAdmin`, `OutboxMetrics` and the schema check are registered always. This is the caller that `RunUntilIdleAsync` needs.

### 11. `RunUntilIdleAsync` reuses the loop in a mode that stops

```csharp
public static Task RunUntilIdleAsync<TContext>(this IServiceProvider services, CancellationToken cancellationToken = default)
    where TContext : DbContext;
```

It reads `OutboxRegistration<TContext>`, throws `InvalidOperationException` when it is missing or has no handler, runs the schema check once inline (throwing on a mismatch or an unreachable database), then runs an `OutboxWorker<TContext>` whose loops are built with `stopWhenIdle: true`. In that mode a loop claims as soon as a slot is free instead of waiting for the poll, and it ends when a claim made with no attempt running returns fewer messages than it asked for and none of them pending. Renewal still runs on the clock, so a long handler keeps its lease. The method returns when every loop has ended, by which point every outcome has been written. It uses the service's `TimeProvider` and does not move it. The cancellation token stops it the way a host stops the workers.

*Alternative:* a separate claim-and-handle routine in the kit. Rejected: it would be a second copy of the attempt handling, and a test that passes through it would prove nothing about the loop the service runs.

### 12. The kit's contract

```csharp
namespace DurableOutbox.Testing;

public sealed class OutboxContract<TContext> where TContext : DbContext
{
    /// addDbContext registers TContext against a database whose outbox table the service's migrations created.
    public OutboxContract(Action<IServiceCollection> addDbContext);

    public Task WrittenWithTheChangeAsync(CancellationToken cancellationToken = default);   // guarantee 1
    public Task AtLeastOnceAsync(CancellationToken cancellationToken = default);            // guarantee 2
    public Task OneOwnerAtATimeAsync(CancellationToken cancellationToken = default);        // guarantee 3
    public Task CrashIsAnAttemptAsync(CancellationToken cancellationToken = default);       // guarantee 4
    public Task NeverDroppedAsync(CancellationToken cancellationToken = default);           // guarantee 5
}
```

Each call builds its own service provider: the service's `addDbContext`, a `FakeTimeProvider`, and `AddDurableOutbox<TContext>` with an internal scripted handler for kinds named `durable-outbox.contract.{guid}`, unique to the call. So calls can run in parallel on one database and never touch the service's messages. Rows are left in the table; the README says to point the kit at a database used only for tests. A failed check throws `InvalidOperationException` whose message starts with the guarantee's number and name. *Alternative:* a public exception type. Rejected: no caller tells it apart from any other failure.

What each method runs, moved from today's tests:

| Method | Scenarios | From |
| :--- | :--- | :--- |
| 1 | A rolled-back transaction leaves no message; a committed one leaves one; `AddAsync` with no transaction throws and writes nothing. | `WritingTests` |
| 2 | A duplicate uniqueness key is not queued and the caller's transaction commits; a message that throws, retries, then completes keeps one ID over three attempts through the real workers. | `WritingTests`, `WorkerLoopTests` |
| 3 | Four concurrent claimers take 1,000 messages exactly once; an outcome under a stale token is refused; a renewed lease is not taken over for 150 seconds. | `ClaimingTests`, `OutcomeTests`, `LeaseTests` |
| 4 | An attempt that never reports is claimed again after its lease, then parked at the attempt limit with each abandoned attempt recorded. | `ClaimingTests` |
| 5 | A message that fails every attempt is parked with each error; an operator retry makes it claimable as attempt 1 with its errors kept. | `OutcomeTests`, new |

Guarantee 1's half about the business change is dropped: the kit cannot know a service's entities, and that half tests PostgreSQL's transactions rather than the library. Guarantee 6 is a promise not made, so it has no method.

The clock-stepping helpers in `WorkerTestBase` (step while every loop is idle, fail after 1,000 steps) move into the kit as an internal `WorkerClock` used by the contract and, through `InternalsVisibleTo("DurableOutbox.Tests")`, by the library's remaining worker tests. The library gains `InternalsVisibleTo("DurableOutbox.Testing")` for the store, the worker and the idle signal. Because the kit uses internals, its dependency on `DurableOutbox` is packed as the exact same version (`[x.y.z]`), set by a small target in the kit's project file and checked in the packed `.nuspec`.

In the library's tests, an abstract `ContractTests<TContext>` has one fact per method, and two subclasses run it against `TestDbContext` (`svc.outbox`) and a new `ContractDbContext` (`contract.messages`) with its own migration. The second also seeds a message of a service kind first and checks it is untouched afterwards.

### 13. Packaging

`Directory.Build.props` gains the shared metadata: `Version` 1.0.0, `Authors` and `Copyright` with the owner's name, `PackageLicenseExpression` MIT, `PackageReadmeFile` README.md, `PackageProjectUrl` and `RepositoryUrl` (filled in when the GitHub repository exists), `IsPackable` false by default, `IncludeSymbols` with `snupkg`, `EnablePackageValidation`, and `ContinuousIntegrationBuild` when `GITHUB_ACTIONS` is set. Source link needs no package: the .NET SDK includes it for GitHub. The two `src` projects set `IsPackable`, `Description`, `PackageTags` and `GenerateDocumentationFile`, so a public member without an XML comment fails the build. `LICENSE` (MIT) and `README.md` sit at the root and are packed into both packages.

The README holds: what the library is and is not, the guarantees, the adoption steps (register, one migration, one handler, and the explicit transaction), the operator API, telemetry and health check, the kit, and the operating notes of section 9.13.

### 14. Pipeline

`.github/workflows/ci.yml`, on every push and pull request: check out, set up .NET from `global.json`, `dotnet build -c Release`, `dotnet test -c Release` (the Ubuntu runner has Docker for Testcontainers), `dotnet pack -c Release -o artifacts`, upload the packages as a build artifact.

`.github/workflows/publish.yml`, run by hand (`workflow_dispatch`) only: the same build, test and pack, in a job bound to a protected environment `nuget` that the owner sets to require their approval. It has `id-token: write`, gets a short-lived key with the `NuGet/login` action and the nuget.org user name from a repository variable, and pushes the `.nupkg` and `.snupkg` files. It creates no tag. Setting up the trusted publishing policy on nuget.org, the environment and the variable is the owner's step, listed in the README's release section.

Action versions are the newest major of each at the time of writing, pinned by major.

## Risks / Trade-offs

- **The kit uses the library's internals.** A kit and library of different versions could break at run time. → The kit pins the exact library version.
- **Filter text compared by normalising `pg_get_expr` output.** A future PostgreSQL could print a filter differently and fail a correct table. → The normaliser and the expected text sit in one class; the check's error shows what it found, so a false mismatch is easy to see. Only PostgreSQL 17 is tested (section 9.15 says so).
- **The sample reads every pending and parked index entry every 15 seconds, in every process that samples.** → Stated in section 9.13; it is the same entries an idle claim reads, at a fifteenth of the rate.
- **Two outbox tables in one process with the same kind** report two gauge series with the same tags. → Stated in section 9.11; kinds are the service's names and rarely repeat across tables.
- **`messages.enqueued` counts inserts, not commits.** → Stated in section 9.11 and 9.13. Counting at commit would need a hook into the caller's transaction.
- **A cancelled message's handler runs until the next renewal**, up to a third of the lease. → Its outcome is refused either way; stated in section 9.10.
- **With `BackgroundServiceExceptionBehavior.Ignore` a schema mismatch does not stop the host.** → The workers still claim nothing, and the error is logged.
- **The publish workflow cannot be run before the repository is pushed.** → It is reviewed here and first run by the owner, with approval, after archiving.
- **Moving the guarantee tests could weaken them.** → Each kit scenario keeps its current assertions; the library's run of the kit against `svc.outbox` must fail where the current tests fail. Task 6.8 checks this by removing the claim's row lock and the completion's fence once more.

## Migration Plan

Nothing is released. Services built against the unreleased library add one migration for the operator index. The registration call is unchanged.
