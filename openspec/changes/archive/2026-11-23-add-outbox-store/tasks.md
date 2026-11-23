# Tasks

## 1. Solution and build settings

- [x] 1.1 Create `DurableOutbox.slnx`, `src/DurableOutbox` (class library) and `tests/DurableOutbox.Tests` (xUnit v3 on the Microsoft Testing Platform, opted into in `global.json`), with `Directory.Build.props` (`net10.0`, nullable, warnings as errors) and central package versions. Verify `dotnet build` succeeds with no warnings.
- [x] 1.2 Add `.gitignore` and `InternalsVisibleTo` for the test project. Verify `git status` shows no build output.
- [x] 1.3 Add the test fixture: one PostgreSQL container (Testcontainers) shared by the run, a fresh database per test, and a `FakeTimeProvider`. Verify with a test that opens a connection to its database.

## 2. Table and registration

- [x] 2.1 Add the internal outbox entity and `ModelBuilder.AddDurableOutbox(schema, table)` with every column, the claim index and the uniqueness index named explicitly (design decision 1). Verify by task 2.3.
- [x] 2.2 Add the per-context SQL holder that reads the schema and table name from the model and quotes them (design decision 2). Verify by the tests in groups 3 and 4 running against a non-default schema and table name.
- [x] 2.3 Add the test `DbContext` (one business table plus the outbox), its design-time factory, and generate its migration with `dotnet ef migrations add`. Verify the migration contains the columns of section 9.3 and both filtered indexes, and that the fixture applies it.
- [x] 2.4 Add `services.AddDurableOutbox<TContext>()` registering the writer, the store, the SQL holder and a default `TimeProvider` (design decision 4). Verify a test scope resolves `IOutbox`.

## 3. Writing a message

- [x] 3.1 Add `IOutbox`, `OutboxMessage` and the writer: the `ON CONFLICT DO NOTHING` insert, the transaction check, the version 7 ID from `now`, and `trace_parent` from the current activity (design decision 3). Verify by the tests below.
- [x] 3.2 Test guarantee 1: a transaction that rolls back leaves neither the business row nor the message, and one that commits leaves both.
- [x] 3.3 Test guarantee 1: `AddAsync` with no transaction throws `InvalidOperationException` and writes nothing.
- [x] 3.4 Test guarantee 2 (uniqueness): a second message with the same kind and key returns `false`, one row exists and the caller's `SaveChanges` and commit still succeed; the same key under another kind is queued.

## 4. Claiming

- [x] 4.1 Add the store's claim operation with the statement from section 9.7, one token per call, and the row verdicts (design decisions 5 and 8). Settle `SqlQueryRaw` or the `NpgsqlCommand` fallback and record which in `design.md`. Verify a test claims a due message as attempt 1 with a token and a due time one lease ahead.
- [x] 4.2 Test guarantee 3: four claimers in separate scopes claim 1,000 messages in batches until none is left, and every ID is returned exactly once.
- [x] 4.3 Test claim order and limit: higher priority first, earliest due within a priority, never more than the limit; other kinds and messages not yet due are untouched.
- [x] 4.4 Test guarantee 4: a claim that never reports is claimed again after the lease as the next attempt with a new token and an abandoned entry in the history, and is parked at the attempt limit and never claimed again. Include parking when the retry window has passed.
- [x] 4.5 Test deadline expiry: a message past its deadline is expired by the claim and not returned; one claimed before its deadline can still be completed after it.
- [x] 4.6 Load 100,000 pending messages with a mix of leased, waiting and due rows, run the claim under `EXPLAIN (ANALYZE, BUFFERS)` by hand, and record the plan and timing in `design.md`. Verify it uses the claim index; if it does not, stop and raise it.

## 5. Outcomes

- [x] 5.1 Add complete, fail and reject to the store, each one fenced statement, with fail choosing retry or park in SQL (design decision 6) and the shared 20-entry trim (design decision 7), also applied to the claim. Verify by the tests below.
- [x] 5.2 Test guarantee 3: an outcome from a claim that was taken over is refused and changes nothing; a second report after done is refused.
- [x] 5.3 Test retry: a failed attempt with attempts left is not claimed before its next due time and is claimed as the next attempt after it, with the error in the history.
- [x] 5.4 Test guarantee 5: a message that fails every attempt is parked on the last report with each error, and a rejected message is `Rejected` with its reason; neither is claimed again and both rows remain.
- [x] 5.5 Test the error cap: after 25 failures the history holds attempts 6 to 25 and `last_error` is the latest.

## 6. Documents

- [x] 6.1 Update `docs/durable-outbox.md`: the trimmed append in 9.7 and 9.8, the fail statement in 9.8, the parameterless registration in 9.6, and any other correction the tests forced. Verify each SQL block matches the statement in the code.
- [x] 6.2 Update section 9.15 and the status line of `docs/durable-outbox.md` to say what is now verified and what is not, and bring `docs/build-plan.md` in line. Verify `dotnet build` and `dotnet test` pass and `openspec validate add-outbox-store --strict` is clean.
