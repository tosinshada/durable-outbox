# retry-policy Specification

## Purpose
Decides when a failed attempt is due again and what error text is kept, so retries back off instead of hammering a failing receiver and no sensitive text is stored or logged (guarantees 4 and 5).

## Requirements

### Requirement: A failed attempt is retried after an exponential backoff with jitter
When a handler returns `Retry` or throws on attempt n, the message SHALL be due again after `min(BackoffBase × 2^(n − 1), BackoffCap)`, multiplied by a random factor between 0.8 and 1.2, from the time the outcome is written.

#### Scenario: Third attempt fails
- **WHEN** a kind has a 5-second backoff base and a 15-minute cap, and attempt 3 of a message fails
- **THEN** the message is not claimed before 16 seconds have passed, and is claimed by the time 24 seconds have passed

#### Scenario: Backoff reaches the cap
- **WHEN** the same kind's message fails on attempt 10
- **THEN** the message is not claimed before 12 minutes have passed, and is claimed by the time 18 minutes have passed

### Requirement: RetryAt sets the next due time and uses an attempt
When a handler returns `RetryAt(at, error)`, the message SHALL be due again at `at`, or at once when `at` has passed. The attempt SHALL count against the attempt limit like any other failure.

#### Scenario: Retry at a given time
- **WHEN** a handler returns `RetryAt` one hour ahead on attempt 1 of a kind with an attempt limit of 3
- **THEN** the message is not claimed before the hour has passed, and is claimed as attempt 2 after it

#### Scenario: RetryAt on the last attempt
- **WHEN** a handler returns `RetryAt` one hour ahead on attempt 3 of a kind with an attempt limit of 3
- **THEN** the message is `Parked` at once

### Requirement: An exception counts as a retry
When a handler throws, other than when its token was cancelled because the lease was lost or the host stopped, the attempt SHALL be recorded as a failed attempt with the exception's type, message and stack trace as its error, and SHALL be retried by the backoff above.

#### Scenario: Handler throws every time
- **WHEN** a kind has an attempt limit of 3 and its handler throws on every attempt
- **THEN** the message is `Parked` after attempt 3, with each exception in its error history

### Requirement: Error text is redacted and cut before it is stored or logged
Every error text from a handler, a thrown exception or a timeout SHALL pass through the service's `RedactError` once, and the result SHALL be cut to 2,000 characters. That text SHALL be what is stored and what is logged. When `RedactError` throws, the library SHALL store and log a fixed text saying the error could not be redacted, and never the original text. The default `RedactError` SHALL return the text unchanged.

#### Scenario: Secret in an exception message
- **WHEN** `RedactError` replaces `token=abc` with `token=***` and a handler throws an exception whose message contains `token=abc`
- **THEN** the stored error contains `token=***` and does not contain `token=abc`

#### Scenario: Long error
- **WHEN** a handler returns `Retry` with an error of 5,000 characters
- **THEN** the stored error is the first 2,000 characters of the redacted text

#### Scenario: RedactError throws
- **WHEN** `RedactError` throws for an error
- **THEN** the stored error is the fixed text and does not contain the original error
