# Spec Delta

## MODIFIED Requirements

### Requirement: Messages ended by the claim are not handled
A message the claim parks or expires SHALL NOT be handed to a handler's `HandleAsync`. For a kind with no give-up hook it SHALL NOT use a slot. For a kind with a give-up hook, the message is held by the claim and SHALL use one of the kind's slots until its end is written. Either way it SHALL be logged with its ID, kind and new status once that status is stored.

#### Scenario: Message past its deadline
- **WHEN** a message's deadline passes before the loop claims it
- **THEN** the handler is never called for it and the message is `Expired`

#### Scenario: Held messages respect the concurrency limit
- **WHEN** a kind with a give-up hook has `MaxConcurrency` 2 and five messages past their deadline are due
- **THEN** at most two hooks run at the same time
