# telemetry-and-health Specification

## Purpose
Lets a service see how its outbox is doing through standard .NET metrics, follow a trace from the request that wrote a message to the handler that delivered it, and report a backlog through its health checks.

## Requirements

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

### Requirement: Every attempt is counted by how it ended
The worker that ran an attempt SHALL count it once in `durable_outbox.attempts`, with an `outcome` tag of: `completed` when its completion stood; `retry` when its failure was recorded and the message is due again; `timed_out` when it ran past the kind's attempt timeout and was recorded as a retry that left attempts; `rejected` when its rejection stood; `parked` when its failure was recorded and left no attempts; `lease_lost` when its lease was lost or its outcome was refused. An attempt left without an outcome for any other reason, because its outcome could not be written or its handler threw after the drain period ended, SHALL NOT be counted by its worker. A claim that finds an attempt that never reported SHALL count it as `abandoned`. So an attempt whose lease ran out while its worker was alive is counted twice, `lease_lost` by its worker and `abandoned` by the claim that took the message over. The worker SHALL also record each attempt it counts in `durable_outbox.attempt.duration` with the same tags, from the start of the handler to the outcome being written, or to the handler's return when nothing was recorded.

#### Scenario: A retry and then a completion
- **WHEN** a handler returns a retry on attempt 1 and done on attempt 2
- **THEN** `durable_outbox.attempts` records one `retry` and one `completed` for that kind, and `durable_outbox.attempt.duration` records one value for each

#### Scenario: An attempt that never reported
- **WHEN** a claim takes a message whose previous attempt never reported
- **THEN** `durable_outbox.attempts` records one `abandoned` for that kind

### Requirement: The gauges are sampled by one query
The three gauges SHALL be read from one grouped query over the table, run every 15 seconds in a process that runs workers or has registered the health check. For each kind that has pending or parked messages, `messages.pending` SHALL be its number of `Pending` messages, `messages.parked` its number of `Parked` messages, and `oldest_due.age` the time since its earliest due `Pending` message became due, or zero when none is due. A message held by a live claim SHALL NOT count as due. Every kind in the table SHALL be reported, whether or not this process has a handler for it. Reading a gauge SHALL return the last sample and SHALL NOT query the database.

#### Scenario: A backlog in one kind
- **WHEN** kind `receipt.send` has three pending messages, the earliest of which became due 90 seconds ago, and one parked message, and a sample is taken
- **THEN** the gauges report 3 pending, 1 parked and an oldest due age of 90 seconds for `receipt.send`

#### Scenario: A message under a live claim
- **WHEN** the only pending message of a kind is claimed and its lease has not run out
- **THEN** that kind's oldest due age is zero

### Requirement: Each attempt is traced as a child of the writer's trace
Each attempt SHALL start an activity named `outbox.handle {kind}` of kind consumer, on an activity source named `DurableOutbox`, before its handler runs, and SHALL stop it when the outcome is written. When the message carries the writer's trace context, the activity's parent SHALL be that context; otherwise the activity SHALL start a new trace. The activity SHALL be current while the handler runs, so work the handler does is part of the same trace. It SHALL carry the message ID, kind, attempt and outcome as tags, and SHALL be marked as an error when the attempt did not complete.

#### Scenario: Trace from the writer to the handler
- **WHEN** a message is added while an activity is current, and later handled
- **THEN** the attempt's activity has the same trace ID as the writer's activity and the writer's activity as its parent, and is current inside the handler

### Requirement: The health check reports a backlog as Degraded and never as Unhealthy
A service SHALL be able to register a health check for its outbox with a threshold age. The check SHALL report `Degraded` when the last sample shows a kind whose oldest due message has waited longer than the threshold, naming that kind, or when the last sample could not be taken. Otherwise it SHALL report `Healthy`, including before the first sample. It SHALL NOT report `Unhealthy`, and SHALL NOT query the database itself.

#### Scenario: Backlog past the threshold
- **WHEN** the threshold is 5 minutes and the last sample shows a kind whose oldest due message became due 6 minutes ago
- **THEN** the health check reports `Degraded` and names that kind

#### Scenario: Nothing waiting too long
- **WHEN** the threshold is 5 minutes and every kind's oldest due age in the last sample is under 5 minutes
- **THEN** the health check reports `Healthy`

#### Scenario: Database unreachable
- **WHEN** the last sample failed because the database could not be reached
- **THEN** the health check reports `Degraded`, not `Unhealthy`
