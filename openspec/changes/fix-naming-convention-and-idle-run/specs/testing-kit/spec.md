# Spec Delta

## MODIFIED Requirements

### Requirement: RunUntilIdleAsync runs the registered handlers until nothing is due
`RunUntilIdleAsync` SHALL run the handlers the service registered for a `DbContext` type, with the registered options, until no message of a registered kind is due and no attempt is running, and SHALL then return with every outcome written. A message that a handler or a give-up hook of one kind adds for another registered kind during the call SHALL be handled before it returns. It SHALL work when `RunWorkers` is false. It SHALL use the service's registered clock and SHALL NOT move it, so a message whose next attempt is in the future SHALL be left for later. It SHALL throw `InvalidOperationException` when no handler is registered for that `DbContext` type. When a claim fails, it SHALL end the run and throw the claim's error, and SHALL NOT wait on the clock to claim again.

#### Scenario: Run until done
- **WHEN** a service with `RunWorkers` false commits three messages, whose handler completes two and retries the third with a backoff, and calls `RunUntilIdleAsync`
- **THEN** the call returns with two messages `Completed` and the third `Pending`, due after its backoff, with one attempt

#### Scenario: No handlers
- **WHEN** a service that registered no handler calls `RunUntilIdleAsync`
- **THEN** the call throws `InvalidOperationException`

#### Scenario: One kind adds a message for another
- **WHEN** two kinds each have a give-up hook that adds a message of the other kind, up to six messages in all, and the first is committed before one call to `RunUntilIdleAsync`
- **THEN** the call returns with all six messages `Parked`

#### Scenario: A claim that fails
- **WHEN** the table passes the start-up check, every claim of it fails, and the clock is never moved
- **THEN** the call throws the claim's error, and the message is still `Pending` with no attempt
