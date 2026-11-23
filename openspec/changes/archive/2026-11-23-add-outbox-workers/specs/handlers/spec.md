# Spec Delta

## Purpose

Lets a service register one handler per message kind with that kind's options, and defines what the library does with each result a handler returns (guarantee 2).

## ADDED Requirements

### Requirement: One handler is registered per kind
`AddDurableOutbox<TContext>` SHALL accept an optional callback in which the service registers handlers with `AddHandler<THandler>(kind, configure)`, one per kind, with that kind's options. The handler type SHALL be resolved from the dependency-injection scope of each attempt. A process SHALL claim only messages of kinds that have a handler registered in it. Messages of any other kind SHALL stay `Pending` and untouched.

#### Scenario: Handler registered for a kind
- **WHEN** a service registers a handler for `receipt.send` and a message of that kind is committed
- **THEN** the workers hand the message to that handler

#### Scenario: Message of a kind with no handler
- **WHEN** a message of kind `invoice.file` is committed and no handler for that kind is registered in the process
- **THEN** the message stays `Pending` with zero attempts

### Requirement: Options are checked when the registration runs
The registration call SHALL throw `ArgumentException` before returning when an option is invalid: an empty or repeated kind, a lease, poll interval, backoff base or attempt timeout that is not positive, a backoff cap below the backoff base, a concurrency limit or attempt limit below 1, a retry window that is not positive, a negative drain timeout, or no error filter.

#### Scenario: Two handlers for one kind
- **WHEN** a service registers two handlers for `receipt.send`
- **THEN** the registration call throws `ArgumentException`

#### Scenario: Concurrency limit of zero
- **WHEN** a service registers a kind with a concurrency limit of 0
- **THEN** the registration call throws `ArgumentException`

### Requirement: Workers run only in a process that asks for them
Workers SHALL start with the host when at least one handler is registered and `RunWorkers` is true, which is the default. When `RunWorkers` is false, or no handler is registered, the registration SHALL provide the writer only and nothing SHALL be claimed.

#### Scenario: Process that only writes
- **WHEN** a service registers a handler with `RunWorkers` set to false, starts its host and commits a message
- **THEN** the message stays `Pending` with zero attempts

### Requirement: Each attempt runs in its own scope with the message's details
Each attempt SHALL run in a new dependency-injection scope, with a handler resolved from that scope. The handler SHALL receive the message ID, kind, record reference, attempt number, creation time and deadline. The ID SHALL be the same on every attempt of a message.

#### Scenario: Two attempts of one message
- **WHEN** a handler returns a retry on attempt 1 and done on attempt 2
- **THEN** both deliveries carry the same message ID, attempts 1 and 2, and each attempt's handler came from a different scope

### Requirement: The handler's result is recorded under its lease
When the handler returns `Done`, the message SHALL be completed. When it returns `Reject` with a reason, the message SHALL be rejected with that reason. When it returns `Retry` or `RetryAt`, or throws, the attempt SHALL be recorded as failed, as described in the retry policy. Each outcome SHALL be written under the attempt's lease token and SHALL be written even when the host is shutting down.

#### Scenario: Handler returns done
- **WHEN** a handler returns `Done()` for a message
- **THEN** the message is `Completed` and is not attempted again

#### Scenario: Handler rejects
- **WHEN** a handler returns `Reject("unknown customer")` on attempt 1
- **THEN** the message is `Rejected` with `unknown customer` as its last error and is not attempted again

### Requirement: Follow-up writes commit with the completion
When the handler returns `Done(record)`, the library SHALL open a transaction on the attempt's `DbContext`, write the fenced completion, run `record`, save the context's changes and commit. When the completion matches no row because the lease was lost, `record` SHALL NOT run and nothing SHALL be written. When `record` or the save throws, the transaction SHALL roll back and the attempt SHALL be recorded as failed with that exception.

#### Scenario: Follow-up write commits with the completion
- **WHEN** a handler returns `Done(record)` and `record` adds a business row
- **THEN** the message is `Completed` and the business row exists

#### Scenario: Lease lost before the completion
- **WHEN** a handler returns `Done(record)` after another claim has taken the message
- **THEN** `record` does not run, no business row is written, and the message stays under the other claim's token

#### Scenario: Follow-up write fails
- **WHEN** a handler returns `Done(record)` and `record` throws
- **THEN** no business row is written, and the message is `Pending` with the exception in its error history
