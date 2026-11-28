# Design

## Context

The operator API (design 9.10) is `OutboxAdmin<TContext>`, scoped, on the service's `DbContext`. `ListAsync` runs `OutboxSql.List`, reads rows into the internal `OutboxListRow` (whose properties name their result columns with `[Column]`, decision 19) and maps each with `ToInfo`. Retry and cancel are one statement each by primary key. See proposal.md for why a read by ID is needed.

## Goals / Non-Goals

**Goals:**
- One read by primary key that returns what the list returns, for any status.

**Non-Goals:**
- A typed result for retry or cancel, or a read that is atomic with them. The boolean they return stays the answer to whether they acted.

## Decisions

1. **The read shares the list's select list, row type and mapping.** `OutboxSql` keeps the column list once and both `List` and the new `Find` use it; `FindAsync` reads into `OutboxListRow` and maps with the same `ToInfo`. A second list of columns or a second mapping could drift from the list, and the naming-convention test of the list (decision 19) then covers the read's columns too. Alternative: an EF Core query on `OutboxMessageRow` — rejected, because the library's statements are raw SQL and the list's mapping would be written twice.
2. **`WHERE id = @id`, no status in the text.** The primary key serves it, so no index of 9.3 is needed and the read is not on the claim path.
3. **Read with `SqlQueryRaw(...).ToListAsync` and take the single row.** `FirstOrDefaultAsync` would make EF Core wrap the statement in a subquery; the key returns at most one row anyway. No transaction is opened, so an open transaction on the context is used as it is.
4. **The contract kit does not gain a method.** The kit covers the guarantees and the give-up hook; the admin operations are tested in `OperatorTests`, and the read's only schema-dependent part, its columns, is the list's, already run against a convention. Adding a public kit method would be a new promise for one statement.
5. **The new interface member is a minor change.** `IOutboxAdmin` is a service the library registers and implements; services resolve it and never implement it. The release is 1.3.0. Recorded as decision 21 in 9.14.
6. **Package validation is turned off.** The owner's choice: `EnablePackageValidation` and `PackageValidationBaselineVersion` are removed from `Directory.Build.props`, so no suppression file is needed. Breaking changes are checked by hand before a release (`docs/releasing.md`). Recorded as decision 22 in 9.14.

## Risks / Trade-offs

- [A read followed by a retry or a cancel can race with the dispatcher or another operator] → Documented on `FindAsync` and in the README: the boolean from retry or cancel is still the answer, the read only explains a false.
- [A service that implemented `IOutboxAdmin` itself, for example a hand-written fake, stops compiling] → The interface is not meant to be implemented by services; the release notes in the design status say so.
