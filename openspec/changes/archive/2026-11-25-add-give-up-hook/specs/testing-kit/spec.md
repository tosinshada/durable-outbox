# Spec Delta

## ADDED Requirements

### Requirement: The kit checks the give-up hook against the service's schema
The kit SHALL offer one method, beside the guarantee methods and under the same rules (its own clock, kinds it generates, an exception naming what differed), that checks the give-up hook: a message parked by a failed last attempt, a message parked at the claim after a last attempt that never reported, and a message expired at the claim each commit a write made by the hook together with the status; and a hook that throws leaves the message `Parked` with the hook's error and without the hook's write. The library's test project SHALL call it against both of its `DbContext` types and SHALL hold no second copy of those checks.

#### Scenario: Two schemas
- **WHEN** the library's tests run the method against a context mapping `svc.outbox` and another mapping `contract.messages`
- **THEN** it completes for both

#### Scenario: Status stored without the hook's write
- **WHEN** the method finds a message `Parked` whose hook returned normally but whose write is missing
- **THEN** it throws an exception naming the give-up hook and the message
