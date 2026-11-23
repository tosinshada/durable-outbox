# Proposal

## Why

The design in `docs/durable-outbox.md` rests on one choice: a claim is a postponed due time, written by a single statement and fenced by a lease token (section 9.2). None of it has been run (section 9.15). This change builds the table, the writer and that statement, and proves them against real PostgreSQL under concurrent claimers, before the workers in the next change are built on top.

## What Changes

- Create the solution: `src/DurableOutbox`, `tests/DurableOutbox.Tests`, and the shared build settings (`net10.0`, nullable, warnings as errors).
- Add `modelBuilder.AddDurableOutbox(schema, table)`: the EF Core configuration for the outbox table, the claim index and the uniqueness index (section 9.3). The service creates the table through its own migration.
- Add `IOutbox.AddAsync` and `OutboxMessage` (section 9.4): one insert in the caller's open transaction, refused when there is none, a duplicate uniqueness key ignored.
- Add `services.AddDurableOutbox<TContext>()` with no options. It registers the writer only. Handlers and options arrive with the workers.
- Add the internal store that workers will call: the claim statement (section 9.7) and the fenced outcomes done, failed and rejected (section 9.8). In this change only the tests call it.
- Correct `docs/durable-outbox.md` where the build shows the design was incomplete: the 20-entry cap on the error history is missing from the SQL in 9.7 and 9.8, and 9.8 does not say how a failed attempt chooses between retry and park.

Not in this change: handlers, the worker loop, lease renewal, backoff, timeouts, the operator API, telemetry, the start-up check and the test kit.

## Capabilities

### New Capabilities

- `outbox-schema`: the table the service owns, its fixed columns and indexes, and the rule that the library never deletes a row.
- `message-writing`: adding a message inside the caller's transaction, refusal outside one, and uniqueness keys.
- `message-claiming`: claiming due messages for a kind under a lease and token, the claim order, recovery of abandoned attempts, and parking and expiry at claim time.
- `attempt-outcomes`: recording done, failed and rejected under the lease token, refusing an outcome from a lost lease, and the error history.

### Modified Capabilities

None. There are no specs yet.

## Impact

- **Code:** new solution and two projects. Nothing exists to break.
- **Public API:** `IOutbox`, `OutboxMessage`, `ModelBuilder.AddDurableOutbox`, `IServiceCollection.AddDurableOutbox<TContext>`. The store is `internal`.
- **Dependencies:** `Npgsql.EntityFrameworkCore.PostgreSQL` in the library. In the tests: xUnit v3, `Testcontainers.PostgreSql`, `Microsoft.Extensions.TimeProvider.Testing`, `Microsoft.EntityFrameworkCore.Design`.
- **Docs:** `docs/durable-outbox.md` sections 9.7, 9.8 and 9.15 and its status line; `docs/build-plan.md` if the scope moves.
- **Guarantees touched (section 5.3):** 1, 3, 4 and 5, and the uniqueness half of 2.
