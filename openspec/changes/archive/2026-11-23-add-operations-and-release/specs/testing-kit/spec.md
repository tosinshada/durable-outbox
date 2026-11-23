# Spec Delta

## Purpose

Lets a service prove the library's guarantees against its own `DbContext`, schema and table, and run its handlers to completion in its own integration tests, without depending on any test framework.

## ADDED Requirements

### Requirement: The guarantee tests are published as methods
The `DurableOutbox.Testing` package SHALL offer one method per guarantee 1 to 5 in section 5.3, run against a `DbContext` type and a database the service supplies, whose outbox table its own migrations created. Each method SHALL complete when the guarantee holds and SHALL throw an exception naming the guarantee and what differed when it does not. The package SHALL NOT depend on a test framework. The methods SHALL use a clock of their own and SHALL NOT wait on real time.

#### Scenario: A service's schema that meets the contract
- **WHEN** a service runs each guarantee method against its `DbContext`, with the table in its own schema and name
- **THEN** every method completes

#### Scenario: A guarantee that does not hold
- **WHEN** a guarantee method finds that a message was claimed by two claimers at once
- **THEN** it throws an exception whose message names guarantee 3 and the message

### Requirement: The guarantee methods leave the service's own messages alone
Each guarantee method SHALL write and claim only messages of kinds it generates for that call, so methods can run at the same time on one database and SHALL NOT claim, change or count the service's own messages. The methods SHALL leave the rows they wrote in the table.

#### Scenario: Run beside the service's messages
- **WHEN** the database already holds pending messages of the service's kinds and a guarantee method runs
- **THEN** those messages are unchanged afterwards

### Requirement: The library's own guarantee tests are the kit's methods
The library's test project SHALL prove guarantees 1 to 5 by calling the kit's methods, against two `DbContext` types with different schemas and table names, and SHALL hold no second copy of those tests.

#### Scenario: Two schemas
- **WHEN** the library's tests run the kit against a context mapping `svc.outbox` and another mapping `contract.messages`
- **THEN** every guarantee method completes for both

### Requirement: RunUntilIdleAsync runs the registered handlers until nothing is due
`RunUntilIdleAsync` SHALL run the handlers the service registered for a `DbContext` type, with the registered options, until no message of a registered kind is due and no attempt is running, and SHALL then return with every outcome written. It SHALL work when `RunWorkers` is false. It SHALL use the service's registered clock and SHALL NOT move it, so a message whose next attempt is in the future SHALL be left for later. It SHALL throw `InvalidOperationException` when no handler is registered for that `DbContext` type.

#### Scenario: Run until done
- **WHEN** a service with `RunWorkers` false commits three messages, whose handler completes two and retries the third with a backoff, and calls `RunUntilIdleAsync`
- **THEN** the call returns with two messages `Completed` and the third `Pending`, due after its backoff, with one attempt

#### Scenario: No handlers
- **WHEN** a service that registered no handler calls `RunUntilIdleAsync`
- **THEN** the call throws `InvalidOperationException`
