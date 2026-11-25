# Design

## Context

See proposal.md for the motivation. The constraints that shape the approach:

- The claim is one statement, sent with no surrounding transaction (AGENTS.md, design 9.7). So the claim cannot run consumer code in its own transaction.
- A claim is a postponed due time (9.2). There is no "in flight" status and no sweeper.
- Every outcome is fenced by `id`, `lease_token` and `status = 'Pending'`.
- Column names are fixed, so there is no column to count hook failures in.
- Today `Storage/OutboxSql.cs` has one `Claim` text, which sets `Parked` or `Expired` itself, and one `Fail` text, which parks inside the statement. `Worker/KindLoop.cs` logs rows the claim ended (`EndedByClaim`) and maps `FailOutcome.Parked` to a metric.
- `Done(record)` (`KindLoop.CompleteWithRecordAsync`) is the existing pattern for "consumer writes commit with a status change": execution strategy, transaction on the scope's `DbContext`, fenced statement first, then the consumer's delegate, save, commit.

## Goals / Non-Goals

**Goals:**

- One routine ends a message with the hook, used by every path that parks or expires.
- A kind with no hook sends exactly the statements it sends today.
- A retry on a kind with a hook stays one statement with no transaction.

**Non-Goals:**

- Counting or bounding hook retries. There is no column for it.
- Running anything after the commit.
- A hook for `Reject`, operator cancel or operator retry.

## Decisions

### 1. Shape: an optional interface on the kind's handler

```csharp
public interface IOutboxGiveUpHandler
{
    Task OnGiveUpAsync(OutboxDelivery delivery, OutboxGiveUpReason reason, CancellationToken cancellationToken);
}

public enum OutboxGiveUpReason { AttemptsUsedUp, RetryWindowPassed, DeadlinePassed }
```

A kind has the hook when the type passed to `AddHandler<THandler>` implements `IOutboxGiveUpHandler`. `KindSettings.From` records that as a flag when the registration runs. There is no new option.

`AttemptsUsedUp` and `RetryWindowPassed` mean the message becomes `Parked`; `DeadlinePassed` means `Expired`. When both the attempts and the window are used up, the reason is `AttemptsUsedUp`. `delivery.Attempt` is the number of attempts made, which is 0 for a message that expired before its first attempt.

The name says "give up" because `Completed`, `Rejected` and `Cancelled` also end a message and the hook does not run for them. The design document already uses the phrase (`@give_up_before`).

Alternative considered: a delegate on `OutboxKindOptions`. Rejected: the hook needs the scope's `DbContext`, which a handler class already gets by injection, and the handler type is already registered as scoped. A delegate would need a service provider parameter and would put the kind's logic in two places.

### 2. A kind with a hook holds a spent or expired message instead of ending it in the claim

This is the design decision requirement 3 asks for. For a kind with a hook the loop sends a second claim text, `ClaimHolding`. It is the same single statement with the same `due` query, lock and order. The only difference is what it writes to a row whose verdict is `Parked` or `Expired`: the row stays `Pending`, takes the claim's fresh `lease_token`, and gets `next_attempt_at = @now + @lease`. Its attempts are not incremented. The statement returns the verdict.

That is a claim in the sense of 9.2: a pending row with a later due time and a fresh token. The worker then ends each held row in its own transaction (decision 4). If the worker dies first, the lease runs out and the next claim holds the row again. Nothing sweeps.

The failed-attempt statement gets the same treatment, `FailHolding`. It records the attempt's error and chooses retry or give-up inside the statement, from the row's own attempt count, as today. When the row is spent it keeps the row `Pending` under the attempt's own token with a full lease and returns that verdict; the worker ends it with the same routine.

Alternatives considered:

- *Run the existing `Fail` inside a transaction with the hook.* It reuses a statement, but it puts a transaction around every failed attempt of a kind with a retry window, and when the hook throws there is one error entry to hold two errors (the attempt's and the hook's). Holding keeps a retry at one statement and stores the attempt's error before the hook runs.
- *Wrap the claim in a transaction with the hooks.* Breaks "the claim is one statement, sent with no surrounding transaction", and would hold row locks on a whole batch while consumer code runs.
- *End in the claim, then run the hook afterwards.* The status and the consumer's write would commit apart, which is the gap being closed.
- *One claim text with a `@hold` parameter.* It would change the statement text for every kind. Two texts keep a kind with no hook byte-for-byte as it is (requirement 6).

### 3. The fenced end statement

```sql
UPDATE app.outbox_messages
   SET status = 'Parked',                        -- or 'Expired'; written in the text
       finished_at = @now, lease_token = NULL,
       last_error = COALESCE(@error, last_error),
       errors = CASE WHEN @error IS NULL THEN errors ELSE <errors with this entry appended> END
 WHERE id = @id AND lease_token = @token AND status = 'Pending';
```

`@error` is null when the hook succeeded. Zero rows means the lease was lost (taken over, or cancelled by an operator): the hook does not run and nothing else is written. This is the only way a message of a kind with a hook becomes `Parked` or `Expired`.

### 4. Ending a held message

One routine in `KindLoop`, used for rows held by the claim and rows held by a failed attempt:

1. Open a new dependency-injection scope and resolve the kind's handler type from it. A new scope, not the attempt's: the failed handler may have left tracked changes in its `DbContext`, and they must not be saved.
2. Under the `DbContext`'s execution strategy, open a transaction, run the end statement with a null error. If it matches no row, stop: the lease was lost.
3. Call `OnGiveUpAsync`, then `SaveChangesAsync`, then commit.

The reason is worked out by the worker from the verdict and the attempt count: `Expired` gives `DeadlinePassed`; `Parked` gives `AttemptsUsedUp` when `attempts >= MaxAttempts`, otherwise `RetryWindowPassed`.

A held message is tracked as a running entry in the recording state. So it uses one of the kind's slots until its end is written, it is not renewed (once the end statement has run, the row lock keeps other claims away, as with `Done(record)`), the test kit's `RunUntilIdleAsync` waits for it, and its token is cancelled at the end of the drain period. The connection count in 9.13 (`MaxConcurrency` plus one per kind) stays true.

Alternative considered: ending without a slot. A full claim is followed by another at once, so slow hooks could pile up without limit and take the service's connection pool.

### 5. A hook that throws

The transaction rolls back, so none of the hook's writes are stored. The worker then sends the end statement once more, on its own, with the error `The give-up hook failed: <exception>` passed through `RedactError` and cut like every other error. The message is `Parked` or `Expired`, with that text as `last_error` and in `errors`. The worker logs it at error level and counts it in `durable_outbox.give_up_hook.failures`.

So requirement 1 holds in one direction without exception: the hook's writes are never stored without the status change. The other direction has this one documented exception: the status is stored without the hook's writes when the hook threw.

Why not keep the message pending and call the hook again until it works:

- It would be claimable forever when the hook has a bug, which requirement 5 forbids, and it would hold a message `Pending` past its attempt limit or deadline.
- A bound needs a counter, and there is no column for one. Adding a column changes every service's migration and the start-up check.
- Failures that pass on their own (a dropped connection, a serialization failure) are already retried inside the service's execution strategy when it has a retrying one, before the hook counts as failed.

What the service does about it: the log and the counter say it happened; a parked message shows the hook's error through `IOutboxAdmin.ListAsync`, and an operator retry gives it a new attempt and a new call to the hook.

Two cases are not hook failures:

- The hook throws after its token was cancelled at the end of the drain period. Nothing is written. The lease runs out and the next claim holds the message again.
- The fallback statement itself cannot be written. It is logged and not retried; the lease runs out and the next claim holds the message again, so the hook gets another call.

### 6. The hook may run more than once

As with `Done(record)`: a retrying execution strategy can call it again, and a hold whose end was never written is held again by a later claim. A message parked, retried by an operator and parked again calls it once per parking. The hook must only touch the database, through the scope's `DbContext`, and must be safe to repeat. It may call `IOutbox.AddAsync`, since a transaction is open; that is how a service queues an alert that commits with the status.

### 7. `Reject` does not take a record

No. The handler decides to reject while it runs, so it can write its record before it returns `Reject`. If the process dies between that write and the rejection, the message is attempted again, the handler sees its record and rejects again: the repeat is harmless. The only thing a record overload would add is fencing against a handler that lost its lease, and every write a handler makes while it runs already has that exposure. No caller needs it, and this repository adds no overload without one. It is additive, so it can be added later if a service shows the need.

### 8. Telemetry

- `durable_outbox.attempts` with `outcome = parked` is counted for an attempt whose failure held the message and whose end then stood, with or without the hook. An end that was refused counts `lease_lost`.
- `durable_outbox.messages.expired` is counted when the expiry is stored. For a kind with no hook that is the claim, as today.
- `durable_outbox.give_up_hook.failures` (counter, tag `kind`) is new.
- The existing `EndedByClaim` log message is written once the end stands. A new error-level message reports a hook failure with the message ID, kind and redacted error.

## Risks / Trade-offs

- [A hold whose end is never written is found by the next claim as an attempt that never reported] → It adds one "did not report" error entry and one `abandoned` count for a message whose attempt did report. There is no column to tell the two apart. It only happens when a worker dies, or the database fails, between the hold and the end. Recorded in 9.13.
- [A hook that throws leaves the service's record unmarked while the message is ended] → Error log, counter, and the error on the row. Stated plainly in 9.5 and the README. An expired message cannot be retried by an operator, so for those the service reconciles from the log or the row.
- [A slow hook holds a slot] → Same as a slow handler; documented. The hook should be a few writes.
- [The reason can differ between two holds of one message] → A message held as spent, whose end was never written, can be past its deadline at the next claim and be expired instead. The reason always matches the status that is stored.
- [The commit succeeds but its acknowledgement is lost] → A retrying execution strategy runs the block again, the end statement matches no row, and the attempt is counted `lease_lost` although it was parked. `Done(record)` has the same limit today.

## Migration Plan

Additive. A service that does not implement the interface sees no change. Release as 1.1.0 by `docs/releasing.md`; package validation runs against the 1.0.0 baseline. Rolling back is removing the interface from the handler, or installing 1.0.0: a message held by 1.1.0 is an ordinary pending row with a lease, which 1.0.0 parks or expires at its next claim.
