# outbox-schema Specification

## Purpose
Defines the outbox table a service adds to its own database: who creates it, which names are fixed, which indexes it carries, and that the library never removes a row from it.

## Requirements

### Requirement: The service owns the table
The library SHALL supply an EF Core configuration that a service applies to its own `DbContext`, naming the schema and table. The service SHALL create the table through its own migrations. The library SHALL NOT create or alter the table.

#### Scenario: Configuration applied to a service's model
- **WHEN** a service calls `AddDurableOutbox(schema: "app", table: "outbox_messages")` in `OnModelCreating` and generates a migration
- **THEN** the migration creates `app.outbox_messages` with the columns in `docs/durable-outbox.md` section 9.3

#### Scenario: Library used before the table exists
- **WHEN** a service adds a message and the table has not been created
- **THEN** the call fails with the database's error and the library does not create the table

### Requirement: Column names are fixed
The column names SHALL be those in section 9.3, whatever naming convention the service's model uses. Only the schema and table name SHALL be the service's choice, and the library's statements SHALL use the names found in the service's EF Core model.

#### Scenario: Two services with different table names
- **WHEN** one `DbContext` maps the outbox to `app.outbox_messages` and another to `billing.outbox`
- **THEN** each writes to and claims from its own table only

### Requirement: The table carries the claim and uniqueness indexes
The configuration SHALL define an index on `(kind, priority DESC, next_attempt_at)` holding only rows with status `Pending`, and a unique index on `(kind, unique_key)` holding only rows with a uniqueness key.

#### Scenario: Generated migration
- **WHEN** a service generates its migration from the configuration
- **THEN** it contains both indexes with their filters

### Requirement: The library deletes nothing
The library SHALL NOT delete a row. A message that reaches a final status SHALL stay in the table with that status and the time it finished.

#### Scenario: A finished message
- **WHEN** a message is completed, rejected, parked or expired
- **THEN** its row is still present with its final status and `finished_at` set
