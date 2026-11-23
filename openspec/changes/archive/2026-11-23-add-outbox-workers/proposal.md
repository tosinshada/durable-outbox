# Proposal

## Why

Change 1 built and proved the table, the writer, the claim and the fenced outcomes, but nothing runs them: a message that is written is never delivered. This change adds the handlers and the workers that call the store, so a service can register a handler and have its messages attempted until they are done or parked. After it, the library does its job end to end, without operator tools (section 5.4, build plan change 2).

## What Changes

- Add the handler contract from section 9.5: `IOutboxHandler`, `OutboxDelivery` and `OutboxResult` with `Done()`, `Done(record)`, `Retry`, `RetryAt` and `Reject`. Each attempt runs in its own dependency-injection scope.
- Replace the parameterless `services.AddDurableOutbox<TContext>()` with `AddDurableOutbox<TContext>(configure)`, where the callback is optional. The options are `PollInterval`, `RedactError`, `RunWorkers`, `DrainTimeout` and `AddHandler<T>(kind, …)` with the per-kind `Lease`, `MaxConcurrency`, `AttemptTimeout`, `MaxAttempts`, `RetryWindow`, `BackoffBase` and `BackoffCap`. Options are checked when the registration call runs. Not breaking: a call with no arguments still compiles and still registers the writer only.
- Add a hosted worker per `DbContext` that runs one loop per kind: claim as many messages as there are free slots, run each on its own task, write its outcome, poll with jitter, and back off when the database is unreachable (section 9.9).
- Add lease renewal every third of the lease. A lost lease cancels the handler's token and nothing is recorded for that attempt. **New:** the token is also cancelled when the lease end passes without a successful renewal, for example while the database is unreachable.
- Add `AttemptTimeout`: the token is cancelled, the handler has 5 seconds to return, then the attempt is recorded as a retry with the error "timed out" and its slot is freed, even if the handler ignores the token.
- Add the retry policy: exponential backoff with jitter and a cap, `RetryAt` honoured, an exception counted as a retry, and all error text passed through `RedactError` and cut to 2,000 characters before it is stored or logged.
- Add `Done(record)`: follow-up database writes that commit in one transaction with the fenced completion, and never run when the lease was lost.
- Add shutdown: stop claiming, keep renewing, drain within `DrainTimeout`, then cancel the remaining handlers. An outcome a handler reached is always recorded.
- Add source-generated log messages for the events the loop handles: the database unreachable, a lease lost, a handler exception, and a message parked or expired by the claim. Moved here from change 3 by the owner's choice.
- Correct `docs/durable-outbox.md` where building the workers shows it incomplete: the renewal statement in 9.8 fences on one token, but a kind's attempts in flight come from many claims with different tokens. It becomes one statement over `(id, token)` pairs. Also add `DrainTimeout` and the option type names to 9.6, the lease-end cancellation to 9.8 and 9.13, and the jitter range of the poll to 9.9.

Not in this change: the operator API, metrics, tracing, the health check, the start-up schema check and the test kit.

## Capabilities

### New Capabilities

- `handlers`: the handler contract, registration of handlers and their per-kind options, checking the options, a scope per attempt, and how each result is recorded, including `Done(record)`.
- `worker-loop`: one loop per kind claiming free slots, polling and backing off, isolation between kinds, shutdown and drain, and the log messages.
- `leases-and-timeouts`: lease renewal, cancelling a handler whose lease is lost or has run out, and the attempt timeout that frees a slot.
- `retry-policy`: when a failed attempt is due again, `RetryAt`, exceptions as retries, and the redaction and cut of error text.

### Modified Capabilities

None. The claim and the outcome statements in `message-claiming` and `attempt-outcomes` are called as they are; their requirements do not change.

## Impact

- **Code:** a worker, the loop, renewal and timeout handling, the options and the handler types in `src/DurableOutbox`. `OutboxStore` gains the renewal statement. Tests for the loop in `tests/DurableOutbox.Tests`.
- **Public API:** `IOutboxHandler`, `OutboxDelivery`, `OutboxResult`, `OutboxOptions`, `OutboxKindOptions`, and the configure callback on `AddDurableOutbox<TContext>`. The loop, the worker and the store stay `internal`.
- **Dependencies:** `Microsoft.Extensions.Hosting.Abstractions` in the library, which section 9.1 already lists. No new dependency in the tests.
- **Docs:** `docs/durable-outbox.md` sections 9.5, 9.6, 9.8, 9.9, 9.13, 9.14 and 9.15 and its status line; `docs/build-plan.md` for the move of the log messages and the status of change 2.
- **Guarantees touched (section 5.3):** 2 (attempted until done, same ID), 3 under renewal and timeout, 4 and 5 as the loop uses them, and 6, which the loop must not break by handling in any promised order.
