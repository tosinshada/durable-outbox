# DurableOutbox agent instructions

These are rules, not suggestions. If a rule blocks the task, stop and ask.

DurableOutbox is a .NET 10 library that implements the transactional outbox pattern on PostgreSQL through EF Core and Npgsql. A service writes a message in the same transaction as its business change. Workers claim each message under a lease and hand it to the service's handler, retrying until it is done or parked. It is an outbox and a worker, not a message bus.

- `docs/durable-outbox.md` is the design: the guarantees (5.3), the scope (5.4 to 5.6), the decisions (6 and 9.14) and the technical design (9). Read it before changing anything. If a change would contradict it, stop and raise it. Change the document first, then the code, in the same change.
- `docs/build-plan.md` is the order of work for version 1.
- Behaviour changes go through OpenSpec (`openspec/`), using the `openspec-*` skills. A change brings only what it uses.

## Never weaken these

- **The six guarantees in section 5.3.** Each has a test against real PostgreSQL. Cite the guarantee when a change touches one.
- **A claim is a postponed due time** (9.2). A claimed message stays `Pending` with a later `next_attempt_at` and a fresh `lease_token`. Don't add an "in flight" status or a sweeper.
- **The claim is one statement** with `FOR UPDATE SKIP LOCKED`, sent with no surrounding transaction. Messages are claimed by state, never by a cursor or "ID greater than the last seen".
- **Every outcome and renewal is fenced:** `WHERE id = … AND lease_token = … AND status = 'Pending'`. Zero rows means the lease was lost: discard the result, and never retry the write without the token.
- **Attempts are counted when claimed,** before the handler runs, so a crash is an attempt.
- **`AddAsync` inserts at once inside the caller's open transaction** and throws when there is none. It never opens its own.
- **The library deletes nothing** and never drops a message. A message that cannot be delivered is parked with its errors.
- **The library calls nothing outside the database.** No transport, no broker, no `LISTEN/NOTIFY`, no message body. Section 5.6 is out of scope: don't add anything from it without asking.
- **Error text passes through the service's `RedactError`** before it is stored or logged.
- **Column names are fixed.** Only the schema and table name are the service's choice, read from the EF Core model.

## Code

- Two packages: `src/DurableOutbox` and `src/DurableOutbox.Testing`, the test kit, which services install and the repository's own tests also use. Tests are in `tests/DurableOutbox.Tests`. Nothing in `src/` references `tests/`.
- The public API is section 9 of the design. Everything else is `internal`. A public type or member is a promise to every user of the package: don't add or change one without changing the design first.
- Code is added with its first caller in `src/` or `tests/`. No option, overload or interface without one. Delete what loses its last caller.
- One type per file, named after the type. Warnings are errors.
- Folders in `src/DurableOutbox` follow the sections of the design: `Registration` (9.6), `Writing` (9.4), `Handlers` (9.5), `Schema` (9.3), `Storage` (the claim and fenced outcomes, 9.7 and 9.8), `Worker` (9.8 and 9.9), `Operator` (9.10) and `Telemetry` (9.11). A file goes in the folder of its first caller's section. Folders never change namespaces: every file in a project stays in that project's root namespace.
- Add no package dependency beyond those in section 9.1 without asking.

## Time

All time comes from the injected `TimeProvider`, in UTC.

- Banned: `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, `DateTimeOffset.UtcNow`, `Stopwatch`, `Thread.Sleep`, `Guid.CreateVersion7()` with no argument, and `now()` or `CURRENT_TIMESTAMP` in SQL. Pass the time as a parameter.
- Delays and timers take the provider: `Task.Delay(d, time, ct)`, `new PeriodicTimer(d, time)`, `time.GetTimestamp()`.
- Timeouts inside EF Core and Npgsql are the only real-time exception.

## Database

Follow the `efcore-patterns` and `optimizing-ef-core-queries` skills.

- PostgreSQL 14 or later only. The service owns the table and its migrations. The library never creates or alters it.
- The library's statements are raw SQL with parameters for every value. A status name may be written in the SQL text, so the partial index is used.
- A query on the claim path must be served by an index in section 9.3. No queries in loops, no sync calls.
- Never hold a transaction open while a handler runs.

## Tests

Apply the `test-audit` skill's bar.

- Tests run against real PostgreSQL in Testcontainers, with a fake `TimeProvider` that is advanced. Never read the real clock or sleep to wait. No mock or fake of the database.
- Before adding a test, name the guarantee or the section 9 behaviour its failure would break. If there is none, don't write it.
- A race is tested where a statement is the guard (the claim, a fenced outcome), with concurrent work on separate connections. A dead worker is a claim that never reports. No load, soak or process-kill tests.
- Don't test configuration defaults, DI wiring, framework behaviour or trivial members. Don't repeat what another test already fails on.
- Tests are xUnit v3, run by `dotnet test`. `DurableOutbox.Testing` itself depends on no test framework.

```bash
dotnet build
```

```bash
dotnet test
```

Docker must be running.

## Done means

- Build and run the affected tests before marking a task done. Report failures as they are.
- Update `docs/durable-outbox.md` in the same change when behaviour, the API, the table or an option changes. Record a new decision in section 6 or 9.14.
- Never publish a package or push a tag without approval.
- Write docs in plain English and explain terms on first use.
