# operator-api Specification

## Purpose
Lets a service's own code see which messages are stuck and act on them: list parked and rejected messages, send one again, or cancel one. The library exposes the operations; who may call them is the service's choice.

## Requirements

### Requirement: Parked and rejected messages can be listed
The operator API SHALL list messages of one status, `Parked` or `Rejected`, optionally of one kind, newest finished first, up to a page size. Each entry SHALL carry the message's ID, kind, record reference, uniqueness key, priority, status, attempts, creation time, deadline, finish time, last error and error history. A page SHALL continue after a given entry, ordered by finish time and then ID, so that no message is skipped or repeated between pages. Listing any other status SHALL be refused with `ArgumentException`. Listing SHALL change nothing.

#### Scenario: Parked messages of one kind
- **WHEN** an operator lists `Parked` messages of kind `receipt.send` and that kind has two parked messages, one completed message and one parked message of another kind exists
- **THEN** exactly the two parked `receipt.send` messages are returned, the more recently finished first, each with its error history

#### Scenario: Paging
- **WHEN** five messages are parked, three of them in the same instant, and an operator lists them two at a time, each page after the last entry of the previous one
- **THEN** the three pages return all five messages, each exactly once, newest first

#### Scenario: Status that has no list
- **WHEN** an operator lists `Pending` messages
- **THEN** the call throws `ArgumentException`

### Requirement: An operator retry makes a finished message claimable again
Retrying a `Parked` or `Rejected` message SHALL set it to `Pending`, due at once, with zero attempts, no lease token and no finish time, and SHALL keep its error history and last error. It SHALL return true. Retrying a message in any other status, or one that does not exist, SHALL change nothing and return false. Its next attempt SHALL be numbered 1 and the kind's attempt limit SHALL apply afresh.

#### Scenario: Retry of a parked message
- **WHEN** a message parked after three failed attempts is retried by an operator
- **THEN** the call returns true, and the next claim returns it as attempt 1 with its three earlier errors still in its history

#### Scenario: Retry of a message that is not finished
- **WHEN** an operator retries a `Pending` message
- **THEN** the call returns false and the message is unchanged

### Requirement: An operator can cancel a message that is not done
Cancelling a `Pending` or `Parked` message SHALL set it to `Cancelled` with its finish time, clear its lease token and return true. Cancelling a message in any other status, or one that does not exist, SHALL change nothing and return false. A cancelled message SHALL never be claimed again.

#### Scenario: Cancel a waiting message
- **WHEN** an operator cancels a `Pending` message that is not claimed
- **THEN** the call returns true, the message is `Cancelled`, and no later claim returns it

#### Scenario: Cancel a completed message
- **WHEN** an operator cancels a `Completed` message
- **THEN** the call returns false and the message stays `Completed`

### Requirement: Cancelling mid-attempt refuses that attempt's outcome
When a message is cancelled while an attempt holds it, the attempt's outcome SHALL be refused and SHALL change nothing (guarantee 3). The worker SHALL treat the attempt as having lost its lease: its handler's token SHALL be cancelled no later than the next lease renewal, and nothing SHALL be recorded for it.

#### Scenario: Cancelled while the handler runs
- **WHEN** a handler is running for a message, an operator cancels the message, and the handler then returns `Done`
- **THEN** the handler's token was cancelled at the next renewal, and the message stays `Cancelled` with no completion recorded
