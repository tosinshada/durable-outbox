# Spec Delta

## Purpose

Records the result of an attempt so that only the current holder of a message's lease can change it, and a message that cannot be delivered ends up parked with its errors instead of lost (guarantees 3 and 5).

## ADDED Requirements

### Requirement: An outcome is accepted only from the current lease token
Every outcome SHALL be one statement that applies only when the message is `Pending` and its lease token equals the token of the claim that is reporting. When it does not apply, the outcome SHALL be reported as refused and the message SHALL be unchanged.

#### Scenario: Outcome from a claim that was taken over
- **WHEN** a message is claimed, its lease runs out, a second claim takes it, and the first claim then reports done
- **THEN** the report is refused and the message is still `Pending` under the second claim's token

#### Scenario: Outcome reported twice
- **WHEN** a claim reports done and then reports done again
- **THEN** the second report is refused and the message is unchanged

### Requirement: Done completes the message
An accepted done outcome SHALL set the message to `Completed`, set `finished_at` and clear the token. A completed message SHALL never be claimed again.

#### Scenario: Attempt succeeds
- **WHEN** a claimed message is reported done under its token
- **THEN** it is `Completed` and later claims do not return it

### Requirement: A failed attempt is retried at the given time
An accepted failed outcome with attempts left SHALL keep the message `Pending`, clear the token, set the due time to the time given, and add the error to the history with the attempt number and the time. The message SHALL be claimable from that time and not before.

#### Scenario: Attempt fails with attempts left
- **WHEN** attempt 1 of a message with an attempt limit of 3 is reported failed, to be retried in 30 seconds
- **THEN** it is not claimed before 30 seconds pass, and is claimed as attempt 2 after

### Requirement: A failed attempt with nothing left parks the message
An accepted failed outcome SHALL set the message to `Parked` with `finished_at`, in the same statement, when the attempt limit is used up or the message was created before the kind's retry window began. The error of every attempt SHALL be in its history.

#### Scenario: Every attempt fails
- **WHEN** a message with an attempt limit of 3 is claimed and reported failed three times
- **THEN** it is `Parked` after the third report, with three errors in its history, and is never claimed again

### Requirement: Reject ends the message for good
An accepted rejected outcome SHALL set the message to `Rejected` with `finished_at`, clear the token and record the reason as its error, whatever attempts are left.

#### Scenario: Handler refuses the message
- **WHEN** attempt 1 of a message is reported rejected with a reason
- **THEN** it is `Rejected` with that reason as its last error and is never claimed again

### Requirement: The error history keeps the most recent 20
The history SHALL hold one entry per failed, rejected or abandoned attempt, each with the attempt number, the time and the error, and SHALL keep only the most recent 20. `last_error` SHALL be the most recent error.

#### Scenario: More than 20 errors
- **WHEN** a message with an attempt limit of 25 fails 25 times
- **THEN** its history holds the errors of attempts 6 to 25 and `last_error` is the error of attempt 25
