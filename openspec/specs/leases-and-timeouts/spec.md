# leases-and-timeouts Specification

## Purpose
Keeps a running attempt's claim alive while its handler works, stops a handler whose claim is gone, and bounds how long one attempt can hold a slot (guarantee 3 under renewal, and slow work).

## Requirements

### Requirement: A running attempt's lease is renewed
While a handler runs, the loop SHALL extend the lease of its message to now plus the kind's `Lease`, every third of the lease. One statement SHALL renew every running attempt of the kind, whichever claim it came from, and each message SHALL be renewed only under the token of the claim that holds it. An attempt SHALL stop being renewed once its handler has returned. A renewed lease SHALL NOT be taken over by another claim.

#### Scenario: Handler runs longer than the lease
- **WHEN** a kind has a 60-second lease, its handler runs for 150 seconds of clock time, and another claimer tries to claim the kind at intervals throughout
- **THEN** the other claimer never receives the message, and the handler's done is accepted

#### Scenario: Attempts from two claims
- **WHEN** two messages of a kind were claimed by two different claims and both handlers run past the first lease
- **THEN** both leases are renewed and neither message is claimed by another claimer

### Requirement: A lost lease stops the handler and records nothing
When a renewal finds that a message is no longer held under its attempt's token, the loop SHALL cancel that handler's token. It SHALL record no outcome for that attempt, and a result the handler returns afterwards SHALL change nothing.

#### Scenario: Message taken over during an attempt
- **WHEN** a handler is running and the message's lease token is replaced, as a takeover by another claim would do, and the next renewal runs
- **THEN** the handler's token is cancelled, and when the handler returns done the message is still `Pending` under the other token

### Requirement: A lease that runs out without renewal stops the handler
When the lease end of a running attempt passes without a renewal that reached the database, the loop SHALL treat the lease as lost: it SHALL cancel the handler's token and record no outcome for that attempt.

#### Scenario: Database unreachable for longer than the lease
- **WHEN** a handler is running and connections to the database are refused from before its next renewal until after its lease end
- **THEN** the handler's token is cancelled once the lease end passes, and nothing is recorded for that attempt

### Requirement: An attempt that runs too long is timed out
When a kind has an `AttemptTimeout` and an attempt runs longer than it, the loop SHALL cancel the handler's token and give the handler 5 seconds to return. If the handler returns a result within those 5 seconds, that result SHALL be recorded. If it throws, or has not returned after the 5 seconds, the attempt SHALL be recorded as failed with the error `timed out` and its slot SHALL be freed, whether or not the handler has returned. A result the handler returns after that SHALL change nothing. A kind with no `AttemptTimeout` SHALL have no time limit per attempt.

#### Scenario: Handler honours the token
- **WHEN** a kind has a 5-minute attempt timeout and its handler waits on its token, and 5 minutes pass
- **THEN** the token is cancelled and the attempt is recorded as failed with `timed out`, and the message is due again by the retry policy

#### Scenario: Handler ignores the token
- **WHEN** a kind with `MaxConcurrency` 1 and a 5-minute attempt timeout has two due messages, the first handler ignores its token, and 5 minutes and 5 seconds pass
- **THEN** the first attempt is recorded as failed with `timed out`, the second message is handed to a handler while the first handler is still running, and when the first handler later returns done its message is not completed
