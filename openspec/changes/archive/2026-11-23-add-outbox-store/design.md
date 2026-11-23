# Design

## Context

The repository holds only documents. `docs/durable-outbox.md` section 9 is the technical design and this change follows it; the decisions below are the ones section 9 leaves open or that the first build has to settle. See `proposal.md` for the motivation.

Constraints from `AGENTS.md`: all time comes from the injected `TimeProvider` and is passed to SQL as a parameter; statements are raw SQL with parameters; code arrives with its first caller, and a test counts as a caller.

## Goals / Non-Goals

**Goals:**

- Show that the claim statement in 9.7 is correct under concurrent claimers on real PostgreSQL, and correct section 9 where it is not.
- Leave a store whose methods the worker loop in the next change can call without changing them.

**Non-Goals:**

- Anything that runs a handler. No loop, renewal, backoff or timeout.
- Performance measurement. One look at the claim's query plan, no load test.

## Decisions

### 1. The table is mapped by an internal entity that the library never tracks

`AddDurableOutbox(schema, table)` maps an `internal` class to the table so that the service's migrations create it. Every column and index name is set explicitly, so a naming-convention plugin in the service cannot change them. The library reads and writes only through raw SQL and never loads the entity.

*Alternative:* ship the DDL as a SQL script. Rejected: decision 9 in section 6 gives the table to the service's migrations.

### 2. The SQL text is built once per `DbContext` type, from the model

A singleton per `TContext` reads the schema and table name of the mapped entity from the EF Core model the first time it is used, quotes them with the provider's identifier helper, and keeps the finished statements. Values are always parameters. Status names are written in the text so the planner can match the partial index.

### 3. `AddAsync` uses the context's own command pipeline

The insert runs through `Database.ExecuteSqlRawAsync`, which uses the context's connection and current transaction. The rows-affected count (1 or 0) is the return value. The check for a transaction is `Database.CurrentTransaction` or an enlisted or ambient transaction; with none, it throws before sending anything.

The ID is `Guid.CreateVersion7(now)` and `created_at` and `next_attempt_at` are the same `now`. `trace_parent` is `Activity.Current?.Id`.

### 4. A parameterless `AddDurableOutbox<TContext>()` arrives now

Without a registration nothing can resolve `IOutbox`. This change adds the call with no options: it registers the writer (scoped), the store and the SQL (singleton), and `TimeProvider.System` if the service registered none. The next change adds the options callback and the handlers.

*Alternative:* have the tests construct internal types. Rejected: the first adoption step in the design is a registration call, and the tests should take the same path as a service.

### 5. The store is one internal class with four operations

| Operation | Returns |
| :--- | :--- |
| Claim (kind, limit, lease, attempt limit, retry window) | The token, and for each row its verdict: claimed, parked or expired |
| Complete (id, token) | Accepted or refused |
| Fail (id, token, error, next due time, attempt limit, retry window) | Retried, parked or refused |
| Reject (id, token, reason) | Accepted or refused |

Each takes the `DbContext` to run on, so the next change can run a completion inside a transaction for `Done(record)`. One token is generated per claim call and shared by its batch, which is what the renewal statement in 9.8 expects. The token is a random UUID.

### 6. A failed attempt decides between retry and park in SQL

Section 9.8 says a retry with attempts used up parks "in the same way" but does not say where the choice is made. It is made in the statement:

```sql
UPDATE … SET status = CASE WHEN attempts >= @max_attempts OR created_at <= @give_up_before
                           THEN 'Parked' ELSE 'Pending' END, …
 WHERE id = @id AND lease_token = @token AND status = 'Pending'
RETURNING status;
```

*Alternative:* the worker reads the attempt number it was given and calls a separate park operation. Rejected: the row is the source of truth for attempts, one statement cannot disagree with it, and it uses the same test as the claim.

`@give_up_before` is `now − RetryWindow`, or a typed null when the kind has no window; a null comparison is not true, so the branch is skipped.

### 7. The 20-entry cap is applied in every statement that appends an error

The SQL in 9.7 and 9.8 appends to `errors` without trimming. The claim (abandoned attempts), fail and reject statements all append through one shared SQL fragment that keeps the last 20 elements of the array. Sections 9.7 and 9.8 are corrected to show it.

### 8. The claim reads its rows with `SqlQueryRaw` on an unmapped row type

The claim returns rows, so it cannot use `ExecuteSqlRawAsync`. `Database.SqlQueryRaw<T>` executes the statement as written when nothing is composed over it, and uses the context's connection. `RETURNING` aliases the columns to the row type's property names.

**Settled in task 4.1:** `SqlQueryRaw` runs the statement as written, for the claim and for the failed outcome's `RETURNING status`. No fallback was needed.

### 9. Tests use a real migration and separate connections

The test project has a small `DbContext` with one business table and the outbox, a design-time factory, and a migration generated by `dotnet ef`. Each test gets a fresh, migrated database in one shared PostgreSQL container. Concurrent claimers each run in their own scope, so each has its own connection. Time is a `FakeTimeProvider` that the test advances.

A dead worker is modelled as a claim that never reports. There is no process-kill test.

## Risks / Trade-offs

- **The claim plan may not use the partial index as hoped.** Rows that are leased or waiting for a retry sit in the index ahead of due rows of lower priority, so the scan can walk past many of them. → **Measured in task 4.6** on PostgreSQL 17, with 100,000 pending rows of the kind, 20,000 of another kind and 200,000 finished rows. The claim always used `ix_outbox_claim`. With due rows at the top priority: 0.43 ms, 142 buffers for the whole statement. With due rows only at the lowest of 3 priorities: the index scan took 0.1 ms and 74 buffers. With nothing due, the scan reads every index entry of the kind: 0.16 ms and 115 buffers with 3 distinct priorities, 2.2 ms and 847 buffers with about 2,000. The cost of an idle claim grows with the kind's pending backlog; it is recorded as an operating note in `docs/durable-outbox.md` section 9.13 and the index is unchanged.
- **A retried claim can strand a batch.** If the claim commits but its reply is lost and the execution strategy sends it again, the first batch is leased under a token nobody holds. → Those messages come back after the lease as abandoned attempts. This costs one attempt each and is within guarantee 4. The claim is not wrapped in a retrying strategy by the library.
- **Timestamps round to microseconds in PostgreSQL.** .NET has 100-nanosecond ticks. → Tests compare times that were read back from the database, or that the fake clock set to whole milliseconds.
- **A service using a naming-convention plugin.** → Covered by explicit names (decision 1), and the test `DbContext` uses a schema and table name different from the examples in the design.

## Migration Plan

None. Nothing is released and no table exists anywhere.
