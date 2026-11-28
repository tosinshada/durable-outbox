# Proposal

## Why

`RetryAsync` and `CancelAsync` return false both when no message has the ID and when the message is in a status they do not apply to. A service must not read the outbox table itself, so it cannot tell an operator which of the two happened: its admin API can only answer "not found" for both, after paging through the parked and rejected lists to look for the ID. A read of one message by its ID, in any status, lets it answer "not found" and "wrong status" apart.

## What Changes

- `IOutboxAdmin` gains `Task<OutboxMessageInfo?> FindAsync(Guid id, CancellationToken cancellationToken)`. It returns the message in any status, filled exactly as an entry of `ListAsync` is, or null when no message has that ID. It is a plain read by primary key: no lock, no transaction of its own, no change.
- `ListAsync`, `RetryAsync` and `CancelAsync` are unchanged, and `ListAsync` still accepts only `Parked` and `Rejected`.
- Nothing about claiming, leases, retries or giving up changes. The table and its migrations are unchanged.
- The package version becomes 1.3.0. A member added to `IOutboxAdmin` breaks a type that implements the interface; the interface is implemented by the library only, so the release is minor.
- Package validation is turned off: `dotnet pack` no longer compares the public API with the 1.0.0 baseline.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `operator-api`: an operator can read one message by its ID, in any status.

## Impact

- `src/DurableOutbox/Operator/IOutboxAdmin.cs`, `Operator/OutboxAdmin.cs`, `Operator/OutboxListRow.cs`, `Storage/OutboxSql.cs`.
- `Directory.Build.props` (version 1.3.0, package validation removed).
- `tests/DurableOutbox.Tests/Operations/OperatorTests.cs`.
- `docs/durable-outbox.md` (status header, 9.3, 9.10, 9.14, 9.15), `docs/releasing.md` (checking for breaking changes), `README.md` ("Fixing stuck messages").
