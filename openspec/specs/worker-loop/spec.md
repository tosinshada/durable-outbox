# worker-loop Specification

## Purpose
Runs the claim, the handlers and the outcomes for each kind, so every committed message is attempted until it is done, refused, expired or parked, without one kind holding up another and without losing work at shutdown or during a database outage (guarantee 2).

## Requirements

### Requirement: A committed message is attempted until it is finished
While the workers run, every committed message of a registered kind SHALL be attempted until it is completed, rejected, expired or parked. Messages SHALL be handled in no promised order: two messages, including two for the same record, MAY be handled in either order or at the same time (guarantee 6).

#### Scenario: Message that fails twice then succeeds
- **WHEN** a handler fails a message on attempts 1 and 2 and returns done on attempt 3, and time moves past each retry
- **THEN** the message is `Completed` after attempt 3, with two errors in its history

### Requirement: Each kind claims only as many messages as it has free slots
Each kind SHALL have its own loop. A loop SHALL claim at most as many messages as the kind has free concurrency slots, and SHALL never have more attempts running than the kind's `MaxConcurrency`, except a handler that ignored its token after a timeout. A slot SHALL be freed when the attempt's outcome has been written or the attempt was abandoned.

#### Scenario: More due messages than slots
- **WHEN** a kind with `MaxConcurrency` 2 has 5 due messages and its handler holds every message until released
- **THEN** exactly 2 handlers are running, and a third starts only after one of them is released

### Requirement: A loop claims again at once while work is waiting, and otherwise polls
When a claim returns as many messages as it asked for, the loop SHALL claim again as soon as a slot is free. Otherwise it SHALL wait `PollInterval`, multiplied by a random factor between 0.8 and 1.2, before claiming again. Messages parked or expired by the claim count towards what it returned.

#### Scenario: New message while the loop is idle
- **WHEN** a loop has found nothing due, a message is then committed, and time moves forward by 1.2 times `PollInterval`
- **THEN** the message has been handed to its handler

### Requirement: Kinds do not hold each other up
A kind whose slots are all held SHALL NOT delay the claiming or handling of another kind.

#### Scenario: One kind's slots are all held
- **WHEN** every slot of kind `receipt.send` is held by a handler that has not returned, and a message of kind `invoice.file` is committed
- **THEN** the `invoice.file` message is handled and completed while the `receipt.send` handlers are still running

### Requirement: Messages ended by the claim are not handled
A message the claim parks or expires SHALL NOT be handed to a handler and SHALL NOT use a slot. It SHALL be logged with its ID, kind and new status.

#### Scenario: Message past its deadline
- **WHEN** a message's deadline passes before the loop claims it
- **THEN** the handler is never called for it and the message is `Expired`

### Requirement: The loop survives the database being unreachable
When a claim or a renewal fails because the database cannot be reached or returns an error, the loop SHALL log the error and keep running. It SHALL wait before the next claim, starting at `PollInterval` and doubling after each consecutive failure up to 30 seconds, and SHALL return to normal polling after the first claim that succeeds. No message SHALL be lost: a claim that was made but not reported is recovered by its lease.

#### Scenario: Database comes back
- **WHEN** connections to the database are refused for several claim attempts and are then allowed again
- **THEN** the loop is still running, and a message committed afterwards is handled and completed

### Requirement: Shutdown drains running attempts
When the host stops, every loop SHALL stop claiming at once and SHALL keep renewing the leases of running attempts. It SHALL wait up to `DrainTimeout` for running handlers to return, then cancel the tokens of those still running, then wait for them to return until the host's own shutdown time ends. An outcome a handler returned SHALL be recorded. Nothing SHALL be recorded for an attempt that ended by cancellation; its message SHALL be recovered by its lease.

#### Scenario: Handler finishes within the drain time
- **WHEN** the host stops while a handler is running, and the handler returns done before `DrainTimeout`
- **THEN** the message is `Completed`, and no message is claimed after the stop began

#### Scenario: Handler still running at the end of the drain time
- **WHEN** the host stops while a handler is running, and `DrainTimeout` passes before it returns
- **THEN** the handler's token is cancelled, nothing is recorded for that attempt, and the message is claimed again after its lease ends

### Requirement: The loop logs what it handles
The loop SHALL write a structured log message, with the message ID, kind and attempt where they apply, when the database cannot be reached, when a lease is lost, when a handler throws, when an attempt times out, and when the claim parks or expires a message. The record reference MAY be logged. Error text SHALL be logged only after it has passed through `RedactError`.

#### Scenario: Handler throws
- **WHEN** a handler throws an exception whose message contains text the service's `RedactError` removes
- **THEN** a log message is written with the message ID, kind and attempt, and its error text is the redacted text
