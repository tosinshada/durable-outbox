# Tasks

## 1. Design document

- [x] 1.1 Update `docs/durable-outbox.md` first: `FindAsync` in 9.10 with its statement, decision 21 in 9.14, and the status header for 1.3.0.

## 2. Library

- [x] 2.1 Add `Find` to `OutboxSql`, sharing the select list with `List`, and verify `dotnet build` passes.
- [x] 2.2 Add `FindAsync` to `IOutboxAdmin` with XML docs (including the race with the dispatcher and other operators) and implement it in `OutboxAdmin` with the list's `ToInfo`; verify `dotnet build` passes with no warnings.

## 3. Tests

- [x] 3.1 Add to `OperatorTests`: a message in each status is returned with that status, and an unknown ID returns null. Verify it passes.
- [x] 3.2 Add to `OperatorTests`: a parked message with several errors is returned by `FindAsync` equal to its `ListAsync` entry. Verify it passes.

## 4. Release

- [x] 4.1 Update the README's "Fixing stuck messages" section.
- [x] 4.2 Set `VersionPrefix` to 1.3.0, remove `EnablePackageValidation` and `PackageValidationBaselineVersion`, and verify `dotnet pack` produces both packages.
- [x] 4.3 Record what was verified in 9.15.

## 5. Whole change

- [x] 5.1 Run `dotnet build` and `dotnet test` with Docker running, and report the results as they are.
