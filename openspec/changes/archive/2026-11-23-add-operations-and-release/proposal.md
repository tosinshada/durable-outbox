# Proposal

## Why

After changes 1 and 2 the library delivers messages end to end, but an operator cannot see or fix what is stuck, nothing reports how the queue is doing, a table that drifted from the configuration fails only when a statement breaks, a service cannot prove the guarantees against its own schema, and there are no packages to install. This change makes the library operable and shippable: the last of version 1 (section 5.4, build plan change 3). It proves no new guarantee, but guarantee 5 gains its operator half: a parked message can be sent again.

## What Changes

- Add the operator API from section 9.10: `IOutboxAdmin` with `ListAsync`, `RetryAsync` and `CancelAsync`, and the types it needs: `OutboxQuery`, `OutboxMessageInfo`, `OutboxAttemptError` and `OutboxMessageStatus`. `ListAsync` lists `Parked` or `Rejected` messages, newest first, filtered by kind and paged by `(finished_at, id)`. A retry resets the attempts and keeps the errors. Cancelling a message mid-attempt clears its token, so that attempt's outcome is refused and its handler is cancelled at the next renewal.
- Add the third index from section 9.3, `(kind, finished_at) WHERE status IN ('Parked', 'Rejected')`, to the EF Core configuration. Services add it with a new migration.
- **Changed rule:** the retry window applies only to a message that has been attempted. Today the claim parks any message older than its kind's window, so an operator retry of a message parked by its window would be parked again with no attempt. The "spent" test becomes `attempts >= @max_attempts OR (attempts > 0 AND created_at <= @give_up_before)`. A retried message gets at least one attempt, and so does a message that waited longer than the window without ever being claimed. Agreed with the owner.
- Add telemetry from section 9.11: the `DurableOutbox` meter with three counters, a duration histogram and three gauges sampled every 15 seconds by one grouped query, and a consumer activity `outbox.handle {kind}` per attempt whose parent is the writer's trace. The claim also reports which messages it found abandoned, so they can be counted.
- Add the health check: `Degraded` when the oldest due message of any kind has waited longer than the service's threshold, or when the last sample failed. Never `Unhealthy`.
- Add the start-up check: the table's columns and indexes are compared with the configuration. A mismatch stops the host with every difference listed. An unreachable database does not: the check is retried with the outage backoff, and the workers claim nothing until it passes. Agreed with the owner.
- Register handlers whatever `RunWorkers` says, so a process that does not run workers can still run them on demand from a test. Workers still start only when `RunWorkers` is true.
- Add `DurableOutbox.Testing`: the guarantee tests 1 to 5 as plain methods a service calls against its own `DbContext`, and `RunUntilIdleAsync`, which runs the registered handlers until nothing is due. The guarantee tests of changes 1 and 2 move into the kit and the library's tests call them, so there is one copy. The kit depends on `Microsoft.Extensions.TimeProvider.Testing` for its clock, which section 9.1 gains. Agreed with the owner.
- Add package metadata, the MIT licence and a README with the adoption steps and the operating notes, so both projects pack with `dotnet pack`.
- Add a GitHub Actions pipeline that builds, tests and packs on every push and pull request, and a publish workflow run by hand behind a protected environment, signing in to nuget.org with Trusted Publishing so no API key is stored. Agreed with the owner. Publishing itself stays a separate, approved step after this change is archived.

## Capabilities

### New Capabilities

- `operator-api`: listing parked and rejected messages, retrying one, cancelling one, and what each does to a message mid-attempt.
- `telemetry-and-health`: the meter and its instruments, the gauges' sampling, the consumer activity per attempt, and the health check.
- `schema-check`: comparing the table with the configuration when the host starts, and what happens on a mismatch or an unreachable database.
- `testing-kit`: the contract methods a service runs against its own schema, and `RunUntilIdleAsync`.

### Modified Capabilities

- `outbox-schema`: the configuration gains the operator index on `(kind, finished_at)` for `Parked` and `Rejected` rows.
- `message-claiming`: a message with no attempts is never parked by the retry window, so an operator retry always gets an attempt.
- `handlers`: with `RunWorkers` false the handlers are still registered, so `RunUntilIdleAsync` can run them; no worker starts.

## Impact

- **Code:** in `src/DurableOutbox`, the admin service and its statements, the meter, the activity source, the gauge sampler, the health check, the schema check and the index. `KindLoop` records metrics and starts the activity, and gains a mode that stops when nothing is due. A new project, `src/DurableOutbox.Testing`. In `tests/DurableOutbox.Tests`, a second `DbContext` with another schema and table, a migration for the new index, and the guarantee tests rewritten as calls into the kit.
- **Public API:** `IOutboxAdmin`, `OutboxQuery`, `OutboxMessageInfo`, `OutboxAttemptError`, `OutboxMessageStatus`, `IHealthChecksBuilder.AddDurableOutbox<TContext>(…)`, and in the kit `OutboxContract<TContext>` and `RunUntilIdleAsync<TContext>`. Section 9 is updated first.
- **Dependencies:** `Microsoft.Extensions.Diagnostics.HealthChecks` in the library, already in section 9.1. `Microsoft.Extensions.TimeProvider.Testing` in `DurableOutbox.Testing`, added to section 9.1 by the owner's choice.
- **Schema:** services add one index through a new migration. The column set does not change.
- **Docs:** `docs/durable-outbox.md` sections 9.1, 9.3, 9.7, 9.8, 9.10 to 9.15 and its status line; `docs/build-plan.md`; a new `README.md` and `LICENSE`.
- **Guarantees touched (section 5.3):** 3 (a cancelled attempt's outcome is refused) and 5 (a parked message can be retried, its errors kept). Guarantees 1 to 5 move into the kit with their tests, unchanged in meaning. Guarantee 4 still holds under the changed window rule, because a crashed attempt has already been counted.
