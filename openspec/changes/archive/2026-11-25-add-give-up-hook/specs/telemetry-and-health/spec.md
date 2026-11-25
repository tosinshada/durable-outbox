# Spec Delta

## MODIFIED Requirements

### Requirement: The library publishes its metrics on the DurableOutbox meter
The library SHALL publish these instruments on a meter named `DurableOutbox`:

| Instrument | Type | Tags |
| :--- | :--- | :--- |
| `durable_outbox.messages.enqueued` | Counter | `kind` |
| `durable_outbox.attempts` | Counter | `kind`, `outcome` |
| `durable_outbox.messages.expired` | Counter | `kind` |
| `durable_outbox.give_up_hook.failures` | Counter | `kind` |
| `durable_outbox.attempt.duration` | Histogram, seconds | `kind`, `outcome` |
| `durable_outbox.messages.pending` | Gauge | `kind` |
| `durable_outbox.messages.parked` | Gauge | `kind` |
| `durable_outbox.oldest_due.age` | Gauge, seconds | `kind` |

`messages.enqueued` SHALL count each message `AddAsync` inserted, when it is inserted; a duplicate SHALL NOT be counted. `messages.expired` SHALL count each message whose expiry was stored: by the claim for a kind with no give-up hook, and by the end written after the claim held it for a kind with one. `give_up_hook.failures` SHALL count each call of a give-up hook that failed and left the message ended without it.

#### Scenario: A message is added
- **WHEN** a service adds a message of kind `receipt.send` and then adds a duplicate of it
- **THEN** `durable_outbox.messages.enqueued` with kind `receipt.send` increases by one

#### Scenario: A message expires
- **WHEN** a claim finds a due message past its deadline
- **THEN** `durable_outbox.messages.expired` with that message's kind increases by one

#### Scenario: A give-up hook throws
- **WHEN** the give-up hook of a kind throws for a message being parked
- **THEN** `durable_outbox.give_up_hook.failures` with that kind increases by one
