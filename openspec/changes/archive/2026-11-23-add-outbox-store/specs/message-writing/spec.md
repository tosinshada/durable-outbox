# Spec Delta

## Purpose

Lets a service record a message in the same database transaction as its business change, so the message exists if and only if the change committed (guarantee 1).

## ADDED Requirements

### Requirement: A message commits with the caller's transaction
`AddAsync` SHALL insert the message at once, on the `DbContext`'s connection, inside the transaction the caller has open. The message SHALL become visible to claimers only when that transaction commits.

#### Scenario: Transaction commits
- **WHEN** a service opens a transaction, saves a business change, adds a message and commits
- **THEN** both the change and the message are in the database

#### Scenario: Transaction rolls back
- **WHEN** a service opens a transaction, saves a business change, adds a message and the transaction rolls back
- **THEN** neither the change nor the message is in the database

### Requirement: Writing outside a transaction is refused
`AddAsync` SHALL throw `InvalidOperationException` when the `DbContext` has no current or enlisted transaction, and SHALL write nothing. It SHALL NOT open a transaction of its own.

#### Scenario: No transaction open
- **WHEN** a service calls `AddAsync` without opening a transaction
- **THEN** `InvalidOperationException` is thrown and no message is written

### Requirement: A duplicate uniqueness key is ignored
When a message with the same kind and uniqueness key already exists, in any status, `AddAsync` SHALL return `false` and write nothing, without failing the caller's transaction. Otherwise it SHALL return `true`. Messages without a uniqueness key SHALL always be queued.

#### Scenario: Same kind and key added twice
- **WHEN** a message is added with kind `receipt.send` and key `order-1`, and a second is added with the same kind and key
- **THEN** the second call returns `false`, one message exists, and the caller's transaction still commits

#### Scenario: Same key under another kind
- **WHEN** two messages are added with key `order-1` and different kinds
- **THEN** both calls return `true` and both messages exist

#### Scenario: No key
- **WHEN** two messages with the same kind and record reference and no uniqueness key are added
- **THEN** both are queued

### Requirement: A new message is pending and due at once
A new message SHALL have status `Pending`, zero attempts, no lease token, and a due time and creation time equal to the time of the call, taken from the injected `TimeProvider`. Its ID SHALL be a version 7 UUID made from that time, and SHALL never change. Priority SHALL default to 0. The deadline and uniqueness key SHALL be optional.

#### Scenario: Message added with defaults
- **WHEN** a message is added with only a kind and a record reference, and its transaction commits
- **THEN** it is `Pending` with priority 0, zero attempts, no deadline, and can be claimed immediately

### Requirement: The writer's trace context is stored
`AddAsync` SHALL store the W3C trace context of the current activity on the message, or nothing when there is none.

#### Scenario: Message added inside an activity
- **WHEN** a message is added while an activity is current
- **THEN** the message's `trace_parent` is that activity's W3C identifier
