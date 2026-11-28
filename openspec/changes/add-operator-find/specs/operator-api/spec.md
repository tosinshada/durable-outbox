# Spec Delta

## ADDED Requirements

### Requirement: One message can be read by its ID
The operator API SHALL return the message with a given ID in any status, `Pending`, `Completed`, `Rejected`, `Parked`, `Expired` or `Cancelled`, with the same fields and the same error history as an entry of the list. It SHALL return nothing when no message has that ID. The read SHALL take no lock, SHALL open no transaction of its own and SHALL change nothing. Inside a transaction the service has open on the same database context, it SHALL read what that transaction sees.

#### Scenario: A message in each status
- **WHEN** an operator reads, by ID, one message in each of the six statuses
- **THEN** each is returned with its own status

#### Scenario: An unknown ID
- **WHEN** an operator reads an ID that no message has
- **THEN** nothing is returned

#### Scenario: The same message as the list shows
- **WHEN** a message is parked after failed attempts and an operator both lists parked messages and reads that message by its ID
- **THEN** the read returns the same fields and the same error history as the list's entry for it
