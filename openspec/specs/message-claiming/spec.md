# message-claiming Specification

## Purpose
Gives due messages of one kind to exactly one claimer at a time, under a lease with a token, and ends messages that can no longer be attempted (guarantees 3 and 4).

## Requirements

### Requirement: A claim leases due messages under a token
A claim SHALL take messages of the requested kind that are `Pending` and due, up to the requested limit, in a single statement with no surrounding transaction. For each claimed message it SHALL add one to the attempts, set the lease token for this claim, and move the due time to now plus the lease. The message SHALL stay `Pending`. A claim SHALL return each message's ID, record reference, attempt number, creation time, deadline and trace context.

#### Scenario: A due message is claimed
- **WHEN** a claim runs for a kind with one due message that has never been attempted
- **THEN** the message is returned with attempt 1, and is `Pending` with a lease token and a due time one lease from now

#### Scenario: Messages that are not claimable
- **WHEN** a claim runs for a kind whose messages are all of another kind, not yet due, or finished
- **THEN** nothing is returned and no message changes

#### Scenario: Limit
- **WHEN** a claim with a limit of 5 runs against 20 due messages
- **THEN** exactly 5 are claimed

### Requirement: A live claim is never given to a second claimer
A message SHALL NOT be claimed again while its lease has not run out, and concurrent claims SHALL never return the same message. A claim SHALL NOT wait for rows another claim has locked.

#### Scenario: Claimed message is not claimed again within its lease
- **WHEN** a message is claimed and a second claim runs before the lease ends
- **THEN** the second claim does not return it

#### Scenario: Four concurrent claimers
- **WHEN** four claimers on separate connections claim in batches from 1,000 due messages until none is left
- **THEN** every message is returned exactly once across all of them

### Requirement: Claim order is priority, then earliest due
Within a kind, a claim SHALL take higher priority first and, within a priority, the earliest due time first. This is the order of claiming only; it makes no promise about the order of handling (guarantee 6).

#### Scenario: Mixed priorities
- **WHEN** a claim with a limit of 2 runs against due messages of priority 0, 5 and 5
- **THEN** the two priority 5 messages are claimed, the earlier due first

### Requirement: An attempt that never reports is recovered and counted
A message whose lease ran out without an outcome SHALL become claimable again. When it is next claimed, the abandoned attempt SHALL be added to its error history with its attempt number and the time, and the new attempt SHALL carry the next attempt number and a new token.

#### Scenario: Lease runs out
- **WHEN** a message is claimed, no outcome is recorded, and time passes beyond the lease
- **THEN** the next claim returns it with attempt 2 and a different token, and its error history has one entry for attempt 1

### Requirement: A message with no attempts left is parked when claimed
When a due message has used its attempt limit, or was created before the kind's retry window began, a claim SHALL set it to `Parked` with `finished_at`, clear its token, and SHALL NOT hand it out for handling. The claim SHALL report it as parked.

#### Scenario: Crash loop ends
- **WHEN** a message with an attempt limit of 3 is claimed three times and no outcome is ever recorded
- **THEN** the fourth claim parks it with three abandoned entries in its error history, and it is never claimed again

#### Scenario: Retry window passed
- **WHEN** a message becomes due after the kind's retry window has passed since it was created
- **THEN** the claim parks it

### Requirement: A message past its deadline expires when claimed
When a due message's deadline has passed, a claim SHALL set it to `Expired` with `finished_at`, SHALL NOT hand it out, and SHALL report it as expired. Expiry SHALL take precedence over parking. A message already claimed when its deadline passes SHALL be allowed to finish.

#### Scenario: Deadline passed before the claim
- **WHEN** a message's deadline passes before it is claimed
- **THEN** the claim expires it and does not return it for handling

#### Scenario: Deadline passes mid-attempt
- **WHEN** a message is claimed, its deadline passes, and it is then reported done under its token
- **THEN** it is `Completed`
