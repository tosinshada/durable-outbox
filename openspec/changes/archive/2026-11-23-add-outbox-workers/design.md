# Design

## Context

Change 1 left an internal store, `OutboxStore<TContext>`, with four fenced statements (claim, complete, fail, reject) that only the tests call, and a parameterless `AddDurableOutbox<TContext>()` that registers the writer. The store takes the next due time, the attempt limit and the retry window as arguments, so all policy lives in the caller. This change is that caller. See `proposal.md` for the motivation and the four specs for the behaviour.

Constraints from `AGENTS.md` that shape the approach: every delay and timer takes the injected `TimeProvider`; no transaction is open while a handler runs; outcomes and renewals are fenced and never retried without the token; the claim stays one statement with no surrounding transaction; no queries in loops; and the only new library dependency allowed is `Microsoft.Extensions.Hosting.Abstractions`, which section 9.1 already lists.

## Goals / Non-Goals

**Goals:**

- A loop whose every wait, timeout and renewal runs on `TimeProvider`, so the tests drive it with a fake clock and never sleep.
- At most one database connection per kind for claiming and renewal, as section 9.13 promises, plus one per running attempt.
- Leave a worker that change 3 can instrument (metrics, the consumer activity) without reshaping it.

**Non-Goals:**

- Telemetry, the health check, the start-up schema check and `RunUntilIdleAsync`. They arrive in change 3.
- Wake-up by `LISTEN/NOTIFY` (section 5.5).
- Any load or soak test. The slow-work and outage tests use one or two kinds and a handful of messages.

## Decisions

### 1. One hosted service per `DbContext`, one loop per kind

`AddDurableOutbox<TContext>` registers an internal `OutboxWorker<TContext> : BackgroundService` when `RunWorkers` is true and at least one handler is registered. Its `ExecuteAsync` starts one `KindLoop` per registered kind and waits for all of them.

*Alternatives:* one hosted service per kind, which needs a registration per kind that the generic host cannot tell apart; one loop for all kinds, which breaks the isolation in section 9.9.

### 2. The loop does both claiming and renewal, one statement at a time

Each loop waits for whichever comes first: a free slot after a full claim, the poll delay, the next renewal, or the host stopping. It then runs one statement in a short dependency-injection scope of its own, so a claim and a renewal of the same kind never overlap and use at most one connection. Handlers run on their own tasks; only their outcomes come back to the loop as "slot free".

*Alternative:* a separate renewal timer task. Rejected: it needs a second `DbContext` and connection per kind, and section 9.13's connection count would be wrong.

### 3. Renewal fences on `(id, token)` pairs, skipping locked rows

Section 9.8 renews with one `@token`, but one token is generated per claim (change 1, decision 5) and a kind's running attempts come from many claims. A token per loop instead would let a late result from a lost claim be accepted when the same loop claims the message again, which breaks guarantee 3. So renewal sends two arrays:

```sql
WITH held AS (
    SELECT m.id
      FROM <table> m
      JOIN unnest(@ids, @tokens) AS h(id, token)
        ON m.id = h.id AND m.lease_token = h.token
     WHERE m.status = 'Pending'
       FOR UPDATE OF m SKIP LOCKED
)
UPDATE <table> m
   SET next_attempt_at = @now + @lease
  FROM held
 WHERE m.id = held.id
RETURNING m.id;
```

The primary key serves it. `SKIP LOCKED` keeps renewal from waiting on a row whose completion transaction is open (decision 6) or that another claimer is taking over; a skipped row counts as not renewed. Section 9.8 is corrected to this statement.

### 4. An attempt is tracked until its handler returns

The loop keeps, per running attempt, the message ID, the claim's token, the lease end and a `CancellationTokenSource`. The lease end starts at claim time plus the lease and moves on each renewal that returns the ID. An attempt leaves the renewal set as soon as its handler returns, before its outcome is written. After each renewal, and on each renewal tick when the renewal failed, the loop cancels:

- every running attempt missing from the renewal's result, as **lease lost**;
- every running attempt whose lease end has passed, as **lease lost**, even when no renewal reached the database. This is the owner's choice recorded in the proposal.

Results for IDs no longer running are ignored. The renewal interval is a third of the lease.

### 5. One cancellation source per attempt, with a reason

Each attempt's token comes from one source that is cancelled for one of three reasons, recorded before cancelling:

| Reason | Set by | What is recorded |
| :--- | :--- | :--- |
| Lease lost | Decision 4 | Nothing, whatever the handler returns. |
| Timed out | `new CancellationTokenSource(AttemptTimeout, time)` | The handler's result if it returns one within 5 seconds; otherwise a retry with `timed out`. |
| Drain ended | Shutdown (decision 10) | The handler's result if it returns one; nothing if it throws. |

On a timeout the attempt waits for the handler or `Task.Delay(5 s, time)`, whichever comes first. If the delay wins, the `timed out` retry is written in a **new** scope, because the handler may still be using its own `DbContext`, and the slot is freed. The handler's task is still observed: when it ends, its result is discarded, any exception is swallowed after logging, and only then is its scope disposed. A handler that throws `OperationCanceledException` while its token is not cancelled is an ordinary exception and counts as a retry.

### 6. Outcomes are written in the attempt's scope, with no token

When the handler returns, its result is written by the store resolved from the attempt's scope, with `CancellationToken.None`. A write that fails is logged and not retried: the message comes back after its lease as an abandoned attempt, which guarantee 4 already allows. `Done(record)` runs under the context's execution strategy:

1. Begin a transaction on the attempt's `DbContext`.
2. Write the fenced completion. If it matches no row, roll back and stop; `record` never runs.
3. Run `record` with the attempt's token, then `SaveChangesAsync`, then commit.
4. If `record`, the save or the commit throws, roll back, clear the change tracker and write a fenced failure with the exception. If the attempt's token was cancelled for a timeout, the failure is `timed out`.

The transaction is opened after the handler returns, so none is open while a handler runs. Changes the handler left tracked in its context are saved with `record`'s; that is the scope's normal unit of work.

### 7. Options are plain classes, copied and checked when the registration runs

`OutboxOptions` (`PollInterval`, `RedactError`, `RunWorkers`, `DrainTimeout`, `AddHandler<THandler>(kind, configure = null)`) and `OutboxKindOptions` (`Lease`, `MaxConcurrency`, `AttemptTimeout`, `MaxAttempts`, `RetryWindow`, `BackoffBase`, `BackoffCap`) are public sealed classes with the defaults of section 9.14 decision 4, and a 20-second `DrainTimeout`. After the callback returns, the registration checks them, throws `ArgumentException` (or `ArgumentOutOfRangeException`) naming the kind and the option, and stores an immutable internal copy as a singleton. Changing the options object after registration has no effect. `THandler` is registered as scoped with `TryAddScoped`.

`AddDurableOutbox<TContext>(Action<OutboxOptions>? configure = null)` replaces the parameterless method, so existing calls still compile.

*Alternative:* `IOptions<T>` with `ValidateOnStart`. Rejected: named options per `TContext` and per kind add machinery, and checking during registration fails earlier, in the line that is wrong.

### 8. Backoff and poll jitter use `Random.Shared`

The retry delay is `min(BackoffBase × 2^(n − 1), BackoffCap) × U(0.8, 1.2)`, computed in `double` seconds and clamped before it is added to now so that a large attempt count cannot overflow. The poll wait is `PollInterval × U(0.8, 1.2)`. The outage backoff is `PollInterval × 2^(failures − 1)`, capped at 30 seconds, with the same jitter. `RetryAt` is passed through as given; a time in the past makes the message due at once.

*Alternative:* inject a random source for deterministic tests. Rejected: no production caller needs it, and the tests assert the range (decision 12).

### 9. Error text goes through one function

One internal function turns any error (a `Retry` or `RetryAt` text, a `Reject` reason, an exception's `ToString()`, or `timed out`) into stored text: `RedactError` once, then a cut to 2,000 UTF-16 characters that does not split a surrogate pair. If `RedactError` throws, the text is `The error text could not be redacted.` The same string is passed to the store and to the logger, so nothing reaches either without the filter. The store's fixed text for an abandoned attempt is the library's own and is not passed through it.

### 10. Shutdown in `ExecuteAsync`

When `stoppingToken` fires, each loop stops claiming at once and keeps renewing. The worker waits for running attempts or `Task.Delay(DrainTimeout, time)`. At the delay it cancels every remaining attempt as **drain ended**, then keeps waiting for handlers and their outcome writes. `BackgroundService.StopAsync` stops waiting when the host's own shutdown token fires; whatever is still running then is left to lease expiry.

### 11. Log messages are source-generated, in this change

`[LoggerMessage]` methods in one internal static class, using `ILogger` from `Microsoft.Extensions.Logging.Abstractions`, which EF Core already brings. Events: database unreachable (claim or renewal), lease lost, handler threw, attempt timed out, message parked or expired by the claim, and outcome write failed. Each carries the message ID, kind and attempt where they exist. The logger comes from `ILoggerFactory` when one is registered, otherwise `NullLoggerFactory`. Change 3 adds metrics and the activity next to these calls.

### 12. Tests drive the real worker with a fake clock and handler signals

The tests register the outbox as a service would, start the worker with `IHostedService.StartAsync`, and stop it with `StopAsync`, which drains and so writes every outcome before the test reads the rows. A test handler, registered for the test's kinds, takes its behaviour per message from a singleton the test sets up: return a result, throw, wait on its token, wait for a release, or ignore the token. It signals "started" and "cancelled" through `TaskCompletionSource`s.

To move the loop forward, a helper advances the `FakeTimeProvider` in steps of a tenth of the poll interval and yields between steps, until an awaited signal completes or the test's cancellation token fires. Nothing waits on the real clock. Timing bounds (backoff, `RetryAt`) are asserted on the stored `next_attempt_at`; change 1 already proved that the claim honours it.

A database outage is made by setting the test database to `ALLOW_CONNECTIONS false` and terminating its backends, then allowing connections again. It touches only that test's database, so tests still run in parallel in one container. Log messages are captured by a small `ILoggerProvider` in the test project.

**Settled in task 3.1:** stepping blindly cannot work without a real-time pause between steps, because the clock would race ahead of database work the test cannot see. So each loop exposes an internal `Idle` signal: complete while the loop is waiting on its clock and no attempt is starting or writing its outcome. The helper takes a step only when every loop is idle. The loop creates its timer before it counts as waiting, so a step is never missed by a timer created late. The worker tests passed 20 runs in a row, and the whole suite 10 more. The helpers fail a test after 1,000 steps rather than letting it run forever.

## Risks / Trade-offs

- **Fake clock steps and timer creation race.** A step taken before the loop creates its timer is not seen by that timer. → The helper steps repeatedly until the signal it waits for; decision 12 names the fallback.
- **A handler that ignores its token after a timeout** keeps a connection and a thread outside `MaxConcurrency`. → Already stated in section 9.5; the slot is freed and the late result discarded.
- **`Done(record)` under a retrying execution strategy** can run `record` more than once. → Section 9.5 already requires `record` to touch only the database; the transaction makes a retry all or nothing.
- **Cancelling at lease end while the database is down** can stop a handler whose lease nobody else has taken. → It costs one attempt; the alternative is a repeated send, which is worse for the receiver.
- **Renewal skipping a locked row** treats it as not renewed. → A row is locked only by its own completion (no longer in the renewal set) or by a claimer taking it over after its lease ended, which is a real loss.
- **Host shutdown before handlers return.** → Their messages come back after the lease as abandoned attempts (guarantee 4).

## Migration Plan

None. Nothing is released. The parameterless registration call keeps compiling because the new callback is optional.
