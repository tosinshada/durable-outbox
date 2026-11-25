# Tasks

## 1. Library

- [x] 1.1 Name every result column of `ClaimedMessage`, `SampleRow`, `OutboxListRow` and `CatalogEntry` with `[Column]`.
- [x] 1.2 Rethrow a failed claim in `KindLoop.ClaimAsync` when the loop was built to stop when idle.
- [x] 1.3 Run the loops in `RunUntilIdleAsync` again until no pending message of a registered kind is due.

## 2. Tests

- [x] 2.1 Add `SnakeCaseDbContext`, registered with `UseSnakeCaseNamingConvention()`, and run the kit's methods against it (`NamingConventionContractTests`). Verify they fail without 1.1.
- [x] 2.2 Add `NamingConventionTests`: the start-up check, the gauges and the operator list read their rows under the convention. Verify it fails without 1.1.
- [x] 2.3 Add to `RunUntilIdleTests`: a claim that fails ends the run with its error. Verify the test never ends without 1.2.
- [x] 2.4 Add to `RunUntilIdleTests`: two kinds whose give-up hooks add a message for each other are run to the end in one call. Verify it fails without 1.3.

## 3. Release documents

- [x] 3.1 Update `docs/durable-outbox.md` (the status header, 9.3, 9.12, decisions 19 and 20 in 9.14, and 9.15) and `README.md`.
- [x] 3.2 Set `VersionPrefix` to 1.2.0 in `Directory.Build.props` and keep the validation baseline at 1.0.0. Verify `dotnet pack` reports no package validation error.

## 4. Whole change

- [x] 4.1 Run `dotnet build` and `dotnet test` with Docker running, and report the results as they are.
