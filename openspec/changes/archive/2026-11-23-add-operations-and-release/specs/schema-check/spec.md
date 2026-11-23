# Spec Delta

## Purpose

Finds a table that does not match the library's configuration when the host starts, instead of when a statement first fails, and keeps the workers from claiming from a table they cannot use.

## ADDED Requirements

### Requirement: The table is compared with the configuration when the host starts
When a host that registered the outbox starts, the library SHALL read the outbox table's columns and indexes from the database catalog and compare them with its configuration. Every column in section 9.3 SHALL exist with its type and nullability. Each of the three indexes SHALL exist with the same columns in the same order and direction, the same uniqueness and the same filter. Extra columns and extra indexes SHALL be allowed. The check SHALL only read: it SHALL NOT create or alter anything.

#### Scenario: Table created by the service's migrations
- **WHEN** a host starts against a table created from the library's configuration
- **THEN** the check passes and the workers start claiming

#### Scenario: Extra column added by the service
- **WHEN** the service has added a nullable column of its own to the table
- **THEN** the check passes

### Requirement: A mismatch stops the host and lists every difference
When the table is missing, or a column or index does not match, the check SHALL fail with an error that names the table and lists every difference it found. The failure SHALL stop the host, and the workers SHALL claim nothing.

#### Scenario: Missing column
- **WHEN** a host starts against a table without the `trace_parent` column
- **THEN** the host stops with an error naming the table and `trace_parent`, and no message is claimed

#### Scenario: Missing index
- **WHEN** a host starts against a table without the index on `(kind, finished_at)`
- **THEN** the host stops with an error naming that index, and no message is claimed

### Requirement: An unreachable database delays the check without stopping the host
When the database cannot be reached during the check, the library SHALL log it and try again, waiting the poll interval after the first failure and doubling after each failure in a row, up to 30 seconds. The host SHALL keep running and the workers SHALL claim nothing until the check passes.

#### Scenario: Database down at start-up
- **WHEN** a host starts while the database refuses connections, and connections are allowed later
- **THEN** the host keeps running, the outage is logged, and once the check passes a committed message is handled
