# Spec Delta

## ADDED Requirements

### Requirement: The library reads its results under any naming convention
The claim, the gauge sample, the operator list and the start-up check SHALL read their result rows correctly when the service's `DbContext` has a naming convention that renames columns, such as `UseSnakeCaseNamingConvention()`. The library SHALL NOT need the service to exclude its types from the convention.

#### Scenario: The guarantees under a snake-case convention
- **WHEN** the library's tests run the kit's methods against a context registered with `UseSnakeCaseNamingConvention()`
- **THEN** every method completes

#### Scenario: The other statements under a snake-case convention
- **WHEN** a context registered with `UseSnakeCaseNamingConvention()` runs the start-up check, takes a gauge sample and lists parked messages
- **THEN** the check finds no difference, the gauges report the pending and parked counts, and the list returns the parked message with its last error
