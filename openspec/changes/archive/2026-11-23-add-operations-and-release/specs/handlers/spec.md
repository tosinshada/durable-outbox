# Spec Delta

## MODIFIED Requirements

### Requirement: Workers run only in a process that asks for them
Workers SHALL start with the host when at least one handler is registered and `RunWorkers` is true, which is the default. When `RunWorkers` is false, or no handler is registered, no worker SHALL start with the host and the host SHALL claim nothing. The handlers and their options SHALL be registered whatever `RunWorkers` says, so that a test can run them with `RunUntilIdleAsync`.

#### Scenario: Process that only writes
- **WHEN** a service registers a handler with `RunWorkers` set to false, starts its host and commits a message
- **THEN** the message stays `Pending` with zero attempts

#### Scenario: Handlers run on demand
- **WHEN** a service registers a handler with `RunWorkers` set to false, commits a message and calls `RunUntilIdleAsync`
- **THEN** the handler is given the message
