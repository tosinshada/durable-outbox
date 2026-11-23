# Tasks

## 1. Public types and registration

- [x] 1.1 Add `Microsoft.Extensions.Hosting.Abstractions` (10.0.x, in line with EF Core) to `Directory.Packages.props` and `src/DurableOutbox`. Verify `dotnet build` succeeds with no warnings.
- [x] 1.2 Add `IOutboxHandler`, `OutboxDelivery` and `OutboxResult` as in section 9.5, with `OutboxResult`'s constructor `private protected` and its cases as internal sealed records. Verify `dotnet build`; behaviour is verified by groups 3 to 6.
- [x] 1.3 Add `OutboxOptions` and `OutboxKindOptions` with the defaults of section 9.14 decision 4 and a 20-second `DrainTimeout`, and change the registration to `AddDurableOutbox<TContext>(Action<OutboxOptions>? configure = null)`: check the options, keep an internal copy, register each handler as scoped, and register the worker only when `RunWorkers` is true and a handler exists (design decision 7). Verify a test that two handlers for one kind, a `MaxConcurrency` of 0 and a backoff cap below the base each throw `ArgumentException`, and that the existing tests still pass with the call that has no arguments.

## 2. Store and policy functions

- [x] 2.1 Add the renewal statement to `OutboxStore` over `(id, token)` arrays with `SKIP LOCKED`, returning the renewed IDs (design decision 3). Verify a test that renews messages from two claims in one call, moves both due times to now plus the lease, and leaves out a message whose token was replaced.
- [x] 2.2 Add the error-text function (redact once, cut to 2,000 characters without splitting a surrogate pair, fixed text when `RedactError` throws) and the backoff calculation (design decisions 8 and 9). Verify by the tests in group 5.

## 3. The worker loop

- [x] 3.1 Add the worker test support: a base that registers the outbox with a handler per test kind and starts and stops the worker, a scripted test handler with "started" and "cancelled" signals, a capturing `ILoggerProvider`, and the clock-stepping helper (design decision 12). Settle whether stepping the clock is reliable or the internal wait signal is needed, and record the answer in `design.md`. Verify by test 3.3 passing 20 runs in a row.
- [x] 3.2 Add `OutboxWorker<TContext>` and the per-kind loop: claim up to the free slots, run each attempt on its own task and scope, write its outcome with no token in the attempt's scope, claim again at once after a full claim, otherwise wait the poll interval with jitter, and log messages the claim parked or expired. Add the source-generated log messages (design decisions 1, 2, 6 and 11). Verify by tests 3.3 to 3.7.
- [x] 3.3 Test guarantee 2: a handler that throws on attempt 1, returns `Retry` on attempt 2 and `Done` on attempt 3 sees the same message ID and attempts 1, 2 and 3 from three scopes, and the message ends `Completed` with two errors.
- [x] 3.4 Test the concurrency limit: with `MaxConcurrency` 2 and 5 due messages held by the handler, exactly 2 handlers run, and a third starts only after one is released.
- [x] 3.5 Test kind isolation: with every slot of one kind held, a message of a second kind is handled and completed, and a message of a third kind with no handler stays `Pending` with zero attempts.
- [x] 3.6 Test `Reject` through the loop, and test that with `RunWorkers` false a committed message stays `Pending` with zero attempts.
- [x] 3.7 Test that a message whose deadline passed before it was claimed is `Expired`, is never given to the handler and is logged.

## 4. Leases and timeouts

- [x] 4.1 Add renewal to the loop every third of the lease, the lease end per attempt, removal from the renewal set when the handler returns, and cancellation as "lease lost" for attempts missing from a renewal or past their lease end, with nothing recorded for them (design decisions 4 and 5). Verify by tests 4.2, 4.3 and 7.4.
- [x] 4.2 Test guarantee 3 under renewal: with a 60-second lease, two messages claimed by two different claims are held by their handlers for 150 seconds while a second claimer calls the store's claim at each renewal interval; it never receives either message, and both handlers' `Done` is accepted.
- [x] 4.3 Test a lost lease: while a handler runs, the test replaces the message's lease token as a takeover would; after the next renewal the handler's token is cancelled, and when it then returns `Done` the message is still `Pending` under the replaced token.
- [x] 4.4 Add `AttemptTimeout`: cancel as "timed out", wait up to 5 seconds on the clock, record the handler's result if it returns one, otherwise write the `timed out` retry in a new scope, free the slot, and dispose the old scope only when the handler finally ends (design decision 5). Verify by tests 4.5 and 4.6.
- [x] 4.5 Test a timeout the handler honours: with a 5-minute timeout, a handler that waits on its token is cancelled at 5 minutes, and the message is `Pending` with `timed out` as its last error and a later due time.
- [x] 4.6 Test a timeout the handler ignores: with `MaxConcurrency` 1 and two due messages, the second message's handler starts after 5 minutes and 5 seconds while the first is still running; when the first is released with `Done`, its message is not completed and has `timed out` as its last error.

## 5. Retry policy

- [x] 5.1 Map `Retry`, `RetryAt` and exceptions to the store's fail with the computed or given due time and the kind's attempt limit and retry window, every error through the error-text function, and the redacted text in the log. Verify by tests 5.2 to 5.4.
- [x] 5.2 Test backoff timing on the stored `next_attempt_at`, with a 5-second base and a 15-minute cap: a message seeded with 2 earlier attempts that fails is due between 16 and 24 seconds after the outcome, and one seeded with 9 is due between 12 and 18 minutes after.
- [x] 5.3 Test `RetryAt`: on attempt 1 of a kind with an attempt limit of 3 the message is due exactly at the time given; on attempt 3 it is `Parked` at once. This also covers parking by the kind's attempt limit through the loop, so there is no separate test for a handler that fails every time.
- [x] 5.4 Test the error text: an exception containing `token=abc` is stored and logged as `token=***` by the service's `RedactError`; a 5,000-character `Retry` error is stored as its first 2,000 characters; and when `RedactError` throws, the stored text is the fixed text and not the original.

## 6. Follow-up writes

- [x] 6.1 Add `Done(record)` under the execution strategy: transaction, fenced completion, `record`, save, commit; roll back and record a failure when `record` or the save throws (design decision 6). Verify by test 6.2.
- [x] 6.2 Test `Done(record)`: when the completion stands, the business row `record` adds commits with it; when the lease token was replaced before the handler returns, `record` does not run and no business row exists; when `record` throws, no business row exists and the message is `Pending` with the exception in its history.

## 7. Outage and shutdown

- [x] 7.1 Add the outage backoff (from the poll interval, doubling to 30 seconds, reset by a successful claim) and the drain in `ExecuteAsync`: stop claiming, keep renewing, cancel as "drain ended" at `DrainTimeout`, keep waiting for outcomes (design decisions 8 and 10). Verify by tests 7.3 to 7.5.
- [x] 7.2 Add to the test fixture a way to refuse and then allow connections to one test's database (`ALLOW_CONNECTIONS` and terminating its backends). Verify by test 7.3.
- [x] 7.3 Test recovery after the database restarts: connections are refused across several claim attempts and then allowed; the worker is still running, logged the outage, and a message committed afterwards is completed.
- [x] 7.4 Test a lease that runs out without renewal: while a handler runs, connections are refused until after its lease end; the handler's token is cancelled at the lease end, and once connections are allowed the message has no outcome recorded for that attempt.
- [x] 7.5 Test drain on shutdown: a handler that returns `Done` within `DrainTimeout` has its message completed and a message that becomes due after the stop is never claimed; a handler still running at `DrainTimeout` has its token cancelled, nothing is recorded, and the message is claimed as attempt 2 after its lease.

## 8. Documents

- [x] 8.1 Update `docs/durable-outbox.md`: 9.5 (exception text, the 5-second grace and what is recorded in it, the token `record` receives), 9.6 (option type names, `DrainTimeout`, the optional callback, checks at registration), 9.8 (the renewal statement over `(id, token)` pairs and cancellation at lease end), 9.9 (poll jitter range, outage backoff, drain), 9.11 (log messages arrive in this change), 9.13 (delivery delay with jitter, repeated sends), and 9.14 (new decisions: renewal pairs, cancellation at lease end, checks at registration, logs in change 2). Verify each SQL block matches the statement in the code.
- [x] 8.2 Update section 9.15 and the status line of `docs/durable-outbox.md`, and `docs/build-plan.md` (status of change 2, logs moved from change 3). Verify `dotnet build` and `dotnet test` pass and `openspec validate add-outbox-workers --strict` is clean.
