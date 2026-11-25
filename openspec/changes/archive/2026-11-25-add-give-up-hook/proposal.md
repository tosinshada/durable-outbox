# Proposal

## Why

The library ends a message on its own in three places: a failed attempt that leaves no attempts or no retry window parks it, a claim that finds a due message already spent parks it, and a claim that finds a message past its deadline expires it. None of these calls the service's code. A service whose record has its own status (a delivery row that must become `Failed`) cannot learn that the library gave up, so it cannot use `MaxAttempts`, `RetryWindow` or `Deadline`. It has to switch them off and count attempts in its handler.

`Done(record)` already lets a handler commit a write with a completion. Giving up has no equivalent.

## What Changes

- A new optional interface, `IOutboxGiveUpHandler`, that a kind's handler class can also implement. The library calls it when it parks or expires a message of that kind, with the `OutboxDelivery` and a reason: `AttemptsUsedUp`, `RetryWindowPassed` or `DeadlinePassed`.
- The hook's database writes commit in one transaction with the status change. The status change is fenced by the lease token and written first; when it is refused, the hook never runs.
- For a kind with a hook, the claim and the failed-attempt statement no longer end a message themselves. They hold it: the message stays `Pending` under a lease, and the worker then ends it in a transaction with the hook. A kind with no hook uses today's statements, unchanged.
- A hook that throws rolls back its writes. The message is then parked or expired without the hook, with the hook's error in its error history, an error log and a new counter, `durable_outbox.give_up_hook.failures`.
- `Reject` does not get a record overload. The design records why.
- No public member changes or disappears. Two public types are added. The package version becomes 1.1.0.
- The repository's test kit gains one method that checks the hook on every path against a service's schema.

Out of scope: a hook for `Reject`, for operator cancel or retry, and anything that runs after the commit, such as sending an alert. A service raises its alert from its own record.

## Capabilities

### New Capabilities
- `give-up-hook`: the optional per-kind hook that runs, in one transaction with the status change, when the library parks or expires a message.

### Modified Capabilities
- `worker-loop`: a message the claim finds spent or expired, of a kind with a hook, is held, uses a slot until its end is written, and is logged once ended.
- `telemetry-and-health`: a new counter for hook failures; `messages.expired` counts when the expiry is stored.
- `testing-kit`: a new method that checks the hook against the service's schema.

## Impact

- Public API (section 9.5 of `docs/durable-outbox.md`): adds `IOutboxGiveUpHandler` and `OutboxGiveUpReason`. Nothing else changes; the 1.0.0 validation baseline stays.
- `src/DurableOutbox`: `Handlers` (the two new types), `Registration/KindSettings.cs` (whether the handler has the hook), `Storage/OutboxSql.cs` and `OutboxStore.cs` (two holding statements and the fenced end), `Worker/KindLoop.cs` (ending a held message), `Telemetry` (one counter, one log message).
- `src/DurableOutbox.Testing`: one new contract method and a handler that implements the hook.
- `tests/DurableOutbox.Tests`: tests for each path against real PostgreSQL.
- `docs/durable-outbox.md`, `README.md`, `Directory.Build.props` (version 1.1.0).
- No new package dependency, no change to the table, columns or indexes.
