# Spec Delta

## MODIFIED Requirements

### Requirement: A message with no attempts left is parked when claimed
When a due message has used its attempt limit, or has been attempted at least once and was created before the kind's retry window began, a claim SHALL set it to `Parked` with `finished_at`, clear its token, and SHALL NOT hand it out for handling. The claim SHALL report it as parked. A message with no attempts SHALL NOT be parked by the retry window, so a message an operator retried, or one that was never claimed while its window passed, gets at least one attempt.

#### Scenario: Crash loop ends
- **WHEN** a message with an attempt limit of 3 is claimed three times and no outcome is ever recorded
- **THEN** the fourth claim parks it with three abandoned entries in its error history, and it is never claimed again

#### Scenario: Retry window passed
- **WHEN** a message that has been attempted once becomes due again after the kind's retry window has passed since it was created
- **THEN** the claim parks it

#### Scenario: Retry window passed before the first attempt
- **WHEN** a message that has never been attempted becomes claimable after the kind's retry window has passed since it was created
- **THEN** the claim returns it as attempt 1
