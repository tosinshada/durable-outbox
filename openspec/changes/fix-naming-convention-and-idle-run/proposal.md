# Proposal

## Why

Fitting the library to a service whose `DbContext` uses `UseSnakeCaseNamingConvention()` showed three faults. They were fixed in that service's copy of the library and are carried back here, each with a test.

1. A naming convention on the service's `DbContext` also applies to the internal types the library reads its results into. EF Core then looks for `created_at` where the statement returns `"CreatedAt"`, every claim throws, and no message is ever handled.
2. When a claim fails, a loop waits on the clock before it claims again. In `RunUntilIdleAsync` the clock is the test's and may never move, so a real error shows up as a test that hangs.
3. In `RunUntilIdleAsync` each kind's loop stops by itself once nothing of its kind is due or running. A handler or a give-up hook of one kind can add a message of another kind whose loop has already stopped, and that message is left unhandled.

## What Changes

- Every property of `ClaimedMessage`, `SampleRow`, `OutboxListRow` and `CatalogEntry` names its result column with `[Column]`, which a naming convention does not override.
- A loop built to stop when idle rethrows a failed claim. The hosted worker is unchanged: it still waits and claims again.
- `RunUntilIdleAsync` runs the loops again until a pass leaves no pending message of a registered kind due at the current time.
- No public member is added, changed or removed. The package version becomes 1.2.0.
- The test project gains a dependency on `EFCore.NamingConventions`, to run the kit against a context with a real convention. Neither package depends on it.

## Capabilities

### Modified Capabilities
- `outbox-schema`: the library's statements read their results under any naming convention.
- `testing-kit`: `RunUntilIdleAsync` throws a failed claim's error, and handles a message that one kind adds for another.

## Impact

- `src/DurableOutbox`: `Storage/ClaimedMessage.cs`, `Telemetry/SampleRow.cs`, `Operator/OutboxListRow.cs`, `Schema/CatalogEntry.cs`, `Worker/KindLoop.cs`.
- `src/DurableOutbox.Testing/OutboxTestingExtensions.cs`.
- `tests/DurableOutbox.Tests`, `Directory.Packages.props`, `Directory.Build.props` (version 1.2.0), `docs/durable-outbox.md` (9.3, 9.12, 9.14, 9.15), `README.md`.
