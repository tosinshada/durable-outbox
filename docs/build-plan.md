# DurableOutbox version 1 build plan

| | |
| :--- | :--- |
| Status | Changes 1 and 2 are built, tested and archived. Change 3 is not started. |
| Date | 2026-11-23 |
| Design | [durable-outbox.md](durable-outbox.md). Section numbers below refer to it. |

Version 1 is built as three OpenSpec changes, in order. Each one ends with a library that builds, passes its tests against real PostgreSQL, and is archived before the next one starts. A change brings only what it uses.

| # | Change | Delivers | Guarantees proved (5.3) |
| :--- | :--- | :--- | :--- |
| 1 | `add-outbox-store` | The table, the writer, the claim and the fenced outcomes | 1, 3, 4, 5, and the uniqueness half of 2 |
| 2 | `add-outbox-workers` | Handlers, registration, the worker loop, leases, timeouts, retries, shutdown | 2, 6, and guarantee 3 under renewal |
| 3 | `add-operations-and-release` | Operator API, telemetry, health, start-up check, the test kit, the NuGet packages | None new. Makes the library operable and shippable. |

## Before the first change

`openspec init` has been run, so `openspec/` exists. Nothing else is needed: the solution, projects and build settings arrive with change 1, their first user.

## How each change runs

1. **Propose** with the `openspec-propose` skill, using the scope below. It writes the proposal, the spec deltas, the design and the tasks under `openspec/changes/<name>/`.
2. **Review** the proposal against the design. If the two disagree, fix the design document first.
3. **Apply** with the `openspec-apply-change` skill. Build and run the tests before marking a task done.
4. **Archive** with the `openspec-archive-change` skill, so the specs in `openspec/specs/` describe what is built.

## Solution layout

```
DurableOutbox.slnx
src/DurableOutbox/             the library                      (change 1)
src/DurableOutbox.Testing/     the contract test kit            (change 3)
tests/DurableOutbox.Tests/     tests against PostgreSQL         (change 1)
```

One test project. It has its own small `DbContext` and migration, as a service would.

## Change 1: `add-outbox-store`

**Goal.** Prove the central design choice (9.2) against real PostgreSQL before anything is built on it. This is the "not yet verified" item in 9.15, and it comes first because every later change depends on it.

**OpenSpec capabilities:** `outbox-schema`, `message-writing`, `message-claiming`, `attempt-outcomes`.

| In scope | Design |
| :--- | :--- |
| Solution, the two projects above, build settings (`net10.0`, nullable, warnings as errors), Testcontainers fixture | 9.1 |
| `modelBuilder.AddDurableOutbox(schema, table)`: the table, the claim index and the uniqueness index | 9.3 |
| `IOutbox.AddAsync` and `OutboxMessage`: one `INSERT … ON CONFLICT DO NOTHING` in the caller's open transaction, refused when there is none. Stores the writer's trace context. | 9.4 |
| The claim statement, as an internal store the tests call directly. Claims, parks, expires and records abandoned attempts. | 9.7 |
| The fenced outcome statements: done, retry, reject, and park when attempts or the retry window are used up. Error history capped at 20. | 9.8 |
| SQL built once from the EF Core model's schema and table name | 9.6 |
| `services.AddDurableOutbox<TContext>()` with no options, registering the writer only | 9.6 |

**Not in this change:** handlers, the worker loop, renewal, backoff calculation, registration options, anything in change 3. The tests pass the next due time and the limits to the store directly.

**Done when** these pass, with a fake `TimeProvider`:

- A commit that fails leaves neither the change nor the message. `AddAsync` with no transaction throws.
- A duplicate unique key is not queued and does not fail the caller's `SaveChanges`.
- Four concurrent claimers take 1,000 messages exactly once.
- An outcome with a stale token changes nothing.
- A claim that never reports is claimed again after its lease, with the abandoned attempt in the error history, and is parked at the attempt limit. This stands in for a killed worker; there is no process-kill test.
- A message that fails every attempt is parked with each error.
- Priority order, and expiry of a message past its deadline.

If the claim statement needs to change to pass, section 9.7 is corrected in this change.

## Change 2: `add-outbox-workers`

**Goal.** A service can register a handler and have its messages delivered. After this change the library does its job end to end, without operator tools.

**OpenSpec capabilities:** `handlers`, `worker-loop`, `leases-and-timeouts`, `retry-policy`.

| In scope | Design |
| :--- | :--- |
| `IOutboxHandler`, `OutboxDelivery`, `OutboxResult` | 9.5 |
| `services.AddDurableOutbox<TContext>(…)`, `AddHandler<T>(kind, …)`, per-kind options, validation at start-up, `RunWorkers` | 9.6 |
| One loop per kind: claim free slots, one scope and task per attempt, poll with jitter, back off when the database is unreachable | 9.9 |
| Lease renewal every third of the lease. A lost lease cancels the handler's token. | 9.8 |
| `AttemptTimeout`, including freeing the slot when a handler ignores the token | 9.5 |
| Backoff with jitter and a cap. An exception counts as a retry. `RedactError` and the 2,000-character cut. | 9.5, 9.8 |
| `Done(record)`: follow-up writes that commit with the completion | 9.5 |
| Shutdown: stop claiming, drain within `DrainTimeout`, always record an outcome that was reached | 9.9 |
| Source-generated log messages for the events the loop handles, moved here from change 3 | 9.9, 9.14 decision 8 |

**Not in this change:** operator API, metrics, tracing, health check, start-up schema check, the test kit.

**Done when** these pass:

- A message is attempted until done, with the same ID on every attempt.
- A renewed lease is not taken over. A handler whose lease is lost has its token cancelled and its result refused.
- An attempt past its timeout is cancelled, recorded as a retry and its slot freed, including when the handler ignores the token.
- A kind with every slot held does not delay another kind.
- Backoff timing. `RetryAt` is honoured and uses an attempt.
- `Done(record)` commits with the completion, and rolls back when the lease is lost.
- Drain on shutdown. Recovery after the database restarts.
- Stored error text has passed through `RedactError`.

## Change 3: `add-operations-and-release`

**Goal.** An operator can see and fix what is stuck, a service can test its own schema against the contract, and the packages are ready for nuget.org.

**OpenSpec capabilities:** `operator-api`, `telemetry-and-health`, `schema-check`, `testing-kit`.

| In scope | Design |
| :--- | :--- |
| `IOutboxAdmin`: list, retry, cancel. The `(kind, finished_at)` index arrives here, with its first query. | 9.10, 9.3 |
| The `DurableOutbox` meter, the three sampled gauges, the consumer activity per attempt. The log messages arrived with change 2. | 9.11 |
| The health check: `Degraded` when the oldest due message is too old, never `Unhealthy` | 9.11 |
| The start-up check that the table's columns and indexes match | 9.11 |
| `DurableOutbox.Testing`: the guarantee tests as plain methods, and `RunUntilIdleAsync`. The guarantee tests from changes 1 and 2 move into the kit and the library's own test project calls them, so there is one copy. | 9.12 |
| Package metadata, MIT licence, README with the adoption steps (registration, one migration, one handler) and the operating notes | 5.1, 9.13 |
| A build-and-test pipeline, and a publish step run by hand | 6, decisions 7 and 11 |

**Done when** these pass, and both packages build with `dotnet pack`:

- An operator retry makes a parked message claimable, with attempts reset and errors kept.
- Cancelling a message mid-attempt makes that attempt's outcome refused.
- An attempt's activity has the writer's trace as its parent.
- The start-up check fails on a table with a missing column or index.
- The kit's contract tests pass against a second `DbContext` with a different schema and table name.

Publishing to nuget.org is a separate, manual step after this change is archived. It needs the owner's approval and API key.

## After version 1

The items in 5.5 (`LISTEN/NOTIFY`, partition helpers, an inbox) each get their own change when they are wanted. None is started here.
