# give-up-hook Specification

## Purpose
Lets a service react, inside the database, when the library gives up on a message: the kind's handler can implement a hook that commits its writes together with the message becoming `Parked` or `Expired`.

## Requirements

### Requirement: A kind's handler can implement a give-up hook
A kind SHALL have a give-up hook when the handler type registered for it also implements `IOutboxGiveUpHandler`. The library SHALL call the hook when it parks or expires a message of that kind, with the message's delivery details and a reason: `AttemptsUsedUp` or `RetryWindowPassed` when the message becomes `Parked`, `DeadlinePassed` when it becomes `Expired`. When both the attempts and the retry window are used up, the reason SHALL be `AttemptsUsedUp`. The hook SHALL run on a handler resolved from a dependency-injection scope of its own, not the scope of the failed attempt. The hook SHALL NOT run when a handler returns `Reject`, when a message completes, or for an operator cancel or retry.

#### Scenario: Last attempt fails
- **WHEN** a kind with a hook has `MaxAttempts` 3 and a message's third attempt returns a retry
- **THEN** the hook is called once with attempt 3 and the reason `AttemptsUsedUp`, and the message is `Parked`

#### Scenario: Retry window passed
- **WHEN** a kind with a hook has a retry window of 1 hour and an attempt fails 2 hours after the message was created, with attempts left
- **THEN** the hook is called with the reason `RetryWindowPassed`, and the message is `Parked`

#### Scenario: Handler rejects
- **WHEN** a handler of a kind with a hook returns `Reject`
- **THEN** the message is `Rejected` and the hook is not called

### Requirement: The hook covers messages the claim finds spent or expired
For a kind with a hook, a due message whose attempts or retry window are used up, or whose deadline has passed, SHALL NOT be ended by the claim statement. The claim SHALL hold it under a lease, still `Pending`, without counting an attempt, and the worker SHALL then end it with the hook. The handler's `HandleAsync` SHALL NOT be called for it.

#### Scenario: Last attempt never reported
- **WHEN** a message's last allowed attempt never reports and its lease runs out
- **THEN** the hook is called with the reason `AttemptsUsedUp`, the message is `Parked`, and its error history has the entry for the attempt that never reported

#### Scenario: Deadline passed
- **WHEN** a message's deadline passes before it is claimed
- **THEN** the hook is called with the reason `DeadlinePassed`, the message is `Expired`, and `HandleAsync` is never called for it

### Requirement: The hook's writes commit with the status change
The library SHALL write the fenced status change first and then run the hook, in one transaction on the scope's `DbContext`, save that `DbContext`'s changes and commit. The hook's writes SHALL NOT be stored unless the status change is stored. A message written with `IOutbox.AddAsync` inside the hook SHALL commit with the status change.

#### Scenario: Record marked with the parking
- **WHEN** the hook sets its record to failed and the message is parked
- **THEN** after the commit the record is failed and the message is `Parked`, and at no point is one stored without the other

### Requirement: The hook never runs when the lease was lost
When the fenced status change matches no row, because the message was taken over or an operator cancelled it, the hook SHALL NOT run and nothing SHALL be written. When a failed attempt's own outcome is refused, the hook SHALL NOT run.

#### Scenario: Cancelled while held
- **WHEN** an operator cancels a message after it was held and before its end is written
- **THEN** the hook is not called and the message stays `Cancelled`

#### Scenario: Last attempt lost its lease
- **WHEN** the last allowed attempt fails after its lease was taken over
- **THEN** its failure is refused and the hook is not called by that worker

### Requirement: A hook that throws does not lose the message or leave it pending
When the hook, the save or the commit throws, the transaction SHALL roll back, so none of the hook's writes are stored. The library SHALL then store the status change without the hook, fenced by the same token, with the error text `The give-up hook failed:` followed by the exception, passed through `RedactError` and cut like every other error, as the message's last error and as an entry in its error history. It SHALL log the failure at error level and count it. A hook that throws after its token was cancelled at the end of the drain period SHALL have nothing recorded; the message is held again after its lease.

#### Scenario: Hook throws
- **WHEN** the hook of a message whose last attempt failed throws
- **THEN** the message is `Parked`, its last error starts with `The give-up hook failed:`, the write the hook made before throwing is not stored, and the message is not claimed again

### Requirement: The hook may run more than once
The hook MAY be called more than once for one message: when a retrying execution strategy repeats the transaction, when a held message's end was never written and a later claim holds it again, and once for each time a message is parked after an operator retry. The documentation SHALL state this, and that the hook must only touch the database.

#### Scenario: Worker dies after holding
- **WHEN** a message is held and its end is never written
- **THEN** after the lease a claim holds it again and the hook is called

### Requirement: A kind without a hook is unchanged
A kind whose handler does not implement the hook SHALL send the same claim and failed-attempt statements as before this change, and its messages SHALL be parked and expired inside those statements, with no transaction and no further statement.

#### Scenario: No hook
- **WHEN** a message of a kind with no hook is claimed past its deadline
- **THEN** the claim statement itself sets it `Expired`
