# Spec Delta

## MODIFIED Requirements

### Requirement: The table carries the claim and uniqueness indexes
The configuration SHALL define an index on `(kind, priority DESC, next_attempt_at)` holding only rows with status `Pending`, a unique index on `(kind, unique_key)` holding only rows with a uniqueness key, and an index on `(kind, finished_at)` holding only rows with status `Parked` or `Rejected`, which serves the operator lists and the parked gauge.

#### Scenario: Generated migration
- **WHEN** a service generates its migration from the configuration
- **THEN** it contains all three indexes with their filters
