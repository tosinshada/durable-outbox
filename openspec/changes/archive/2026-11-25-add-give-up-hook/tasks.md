# Tasks

## 1. Design document first

- [x] 1.1 Update `docs/durable-outbox.md`: 5.4 (Failure row), 9.5 (the interface, the reason enum, the transaction, the throw rule, "may run more than once", "only touch the database", raise alerts from your own record), 9.7 (`ClaimHolding`), 9.8 (`FailHolding` and the end statement), 9.9 (a held message uses a slot), 9.11 (the new counter, when `expired` and `parked` are counted, the new log message). Verify each section reads consistently with design.md decisions 1 to 8.
- [x] 1.2 Add decisions to 9.14 (hold instead of end in the claim; hook failure ends the message without the hook; `Reject` takes no record; held messages use a slot) and limits to 9.13 (a hold that is never ended is recorded as an abandoned attempt; an expired message whose hook failed cannot be retried). Verify every decision in design.md has a row.

## 2. Public types and registration

- [x] 2.1 Add `Handlers/IOutboxGiveUpHandler.cs` and `Handlers/OutboxGiveUpReason.cs` with XML docs. Verify `dotnet build` passes with package validation against the 1.0.0 baseline.
- [x] 2.2 Add the hook flag to `KindSettings`, set in `KindSettings.From` from the handler type. Verified by the tests in section 5, which only pass when a hook kind is detected.

## 3. Storage

- [x] 3.1 Add `ClaimHolding`, `FailHolding` and the end statement (status in the text, nullable `@error`) to `OutboxSql`, leaving `Claim` and `Fail` unchanged. Verify with `git diff` that the two existing texts are untouched.
- [x] 3.2 Add the store methods: claim and fail take whether to hold, fail reports a held verdict (new `FailOutcome` value), and a fenced `EndAsync(id, token, status, error)`. Verified by the tests in section 5.

## 4. Worker

- [x] 4.1 In `KindLoop.ClaimAsync`, for a kind with a hook, start an ending for each row returned with a `Parked` or `Expired` verdict, tracked as a running entry in the recording state; move the `EndedByClaim` log and the `expired` count to when the end stands. Verify existing `WorkerLoopTests` and `TelemetryTests` still pass.
- [x] 4.2 Add the ending routine: new scope, execution strategy, transaction, fenced end, `OnGiveUpAsync`, save, commit; reason from verdict and attempt count; zero rows means lease lost. Call it from the failed-attempt path when `FailHolding` reports a hold. Verified by tests 5.1 and 5.2.
- [x] 4.3 Handle a hook that throws: clear the change tracker, send the end statement with the redacted `The give-up hook failed:` text, log at error level (new `OutboxLog` message), count `give_up_hook.failures` (new `OutboxMetrics` counter). Skip all of it when the token was cancelled for the end of the drain. Verified by tests 5.1 (throw case) and 5.4.

## 5. Tests against real PostgreSQL

Each test names the behaviour its failure would break: the give-up hook of section 9.5, and guarantee 5 for the throw case.

- [x] 5.1 Add `OutboxContract.GiveUpHookAsync` to `DurableOutbox.Testing`, with a contract handler that implements the hook and writes a follow-up message through `IOutbox.AddAsync`. It checks: parked on a failed last attempt, parked at the claim after a last attempt that never reported, expired at the claim (handler never called), each with the hook's write stored and the right reason; and a hook that throws leaves the message `Parked` with the hook's error and no write. Call it from `ServiceSchemaContractTests` and `OtherSchemaContractTests`. Verify both pass, and that sending the plain claim for a hook kind makes them fail.
- [x] 5.2 Add `tests/DurableOutbox.Tests/Outcomes/GiveUpHookTests.cs`: the hook does not run when a held message is cancelled before its end is written, and when the last attempt's failure is refused after its lease was taken over; the reason is `RetryWindowPassed` when the window has passed with attempts left; a hook that throws leaves a message that is not claimed again. Verify with `dotnet test`.
- [x] 5.3 Add a worker-loop test: with `MaxConcurrency` 2 and five expired messages, at most two hooks run at once. Verify with `dotnet test`.
- [x] 5.4 Add a telemetry test: a hook that throws counts one `give_up_hook.failures`, and a parked attempt on a hook kind counts `parked`. Verify with `dotnet test`.

## 6. Release documents

- [x] 6.1 Update `README.md`: a "When the library gives up" section with an example handler, the rules (same transaction, may run more than once, database only, what a throw does, raise the alert from your own record), the `MaxAttempts`, `RetryWindow` rows of the options table and the `Deadline` text pointing at the hook, and the new counter in the metrics table. Verify the example compiles by matching it against the test handler.
- [x] 6.2 Set `VersionPrefix` to 1.1.0 in `Directory.Build.props`, keep the validation baseline at 1.0.0, and update 9.15 and the status header of `docs/durable-outbox.md` with what was verified. Verify `dotnet pack` reports no package validation error.

## 7. Whole change

- [x] 7.1 Run `dotnet build` and `dotnet test` with Docker running, and report the results as they are, failures included.
