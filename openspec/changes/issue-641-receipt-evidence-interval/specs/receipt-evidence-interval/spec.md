# Receipt Evidence Interval

## Requirements

### Requirement: Bounded receipt evidence belongs to one active interval

The Runtime Event Queue MUST associate attempts, retries, and the latest normalized retry reason with the active bounded waiter interval for one receipt-bearing record. When a bounded waiter begins after no bounded interval is active, the queue MUST create a new interval with empty evidence. Evidence MUST be removed when that interval ends.

#### Scenario: A new waiter starts with clean evidence

- **WHEN** a bounded waiter for record `input-1` has already ended through timeout or cancellation
- **AND** the queue record remains retained
- **THEN** a later bounded waiter for `input-1` MUST start with zero prior attempts and no prior latest reason
- **AND** a timeout from the later waiter MUST report only evidence observed during the later interval

#### Scenario: Coalesced waiters share the active interval

- **WHEN** two bounded callers wait for the same retained record before its active interval ends
- **THEN** both callers MUST observe the existing coalesced waiter outcome
- **AND** their timeout evidence MUST describe one shared interval rather than two independently counted delivery histories

### Requirement: Only an active bounded waiter may create or update evidence

A delivery attempt or retryable verdict MUST update receipt evidence only while its record has an active bounded waiter interval. A retryable delivery that settles after timeout or cancellation, when no bounded interval is active, MUST NOT create evidence or repopulate evidence removed from the completed interval.

#### Scenario: Late retryable delivery cannot recreate ownerless evidence

- **WHEN** a bounded waiter for `input-1` times out while delivery attempt one is still leased
- **AND** the late delivery then settles with a retryable transport failure while no waiter is active
- **THEN** the queue MUST retain `input-1` for its existing retry path
- **AND** the late verdict MUST NOT recreate attempts, retries, or latest-reason evidence for `input-1`

#### Scenario: Late retryable delivery cannot alter a new interval

- **WHEN** the old interval for `input-1` has ended
- **AND** a new bounded waiter has started with clean evidence
- **AND** the old delivery lease later settles with a retryable verdict
- **THEN** that old verdict MUST NOT increment the new interval's attempts or replace its latest reason
- **AND** only delivery outcomes observed for the new interval MAY contribute to its timeout evidence

### Requirement: Queue record and delivery semantics remain unchanged

Ending a local evidence interval MUST NOT remove, rewrite, reorder, or cancel the retained queue record or its delivery lease. Existing matching receipt validation, permanent refusal, retry scheduling, FIFO ordering, and queue capacity behavior MUST remain unchanged.

#### Scenario: Late matching receipt retires the retained record without reopening work

- **WHEN** a bounded waiter times out or is cancelled
- **AND** its retained record later receives a matching receipt under the existing identity rules
- **THEN** the queue MAY retire the record through its normal acknowledgement path
- **AND** the completed Workflow task MUST NOT be reopened
- **AND** no Runtime invocation MUST be started by that late receipt

#### Scenario: Permanent refusal remains definitive

- **WHEN** a retained receipt-bearing record receives the existing permanent refusal response
- **THEN** the queue MUST reject the active waiter with `AlreadyConsumedRuntimeEventError`
- **AND** it MUST NOT convert that outcome into receipt-budget exhaustion
- **AND** no new evidence interval MUST be created by the refusal

#### Scenario: Retryable response remains eligible for retry

- **WHEN** an active interval observes an empty response, a non-matching response, a delivery timeout, or a retryable transport error
- **THEN** the queue MUST keep the record eligible for its current retry behavior
- **AND** it MUST update the active interval's latest reason using the existing normalization
- **AND** it MUST not extend the bounded wait beyond its original budget

### Requirement: Evidence is volatile and has no new persistence boundary

Receipt evidence MUST remain process-local and MUST be discarded when its active interval ends or the queue stops. This capability MUST NOT add persistence, an Outbox, a journal, a snapshot, a receipt store, a second queue, or a new Server/API/schema contract.

#### Scenario: Queue stop discards interval evidence

- **WHEN** the queue stops while a bounded receipt waiter is active
- **THEN** the waiter MUST end through the existing queue-stop behavior
- **AND** its local evidence MUST be discarded
- **AND** no evidence MUST be recoverable from a new process

### Requirement: Interval boundaries are verified with deterministic time

Focused tests for this capability MUST use the queue's injectable time seam and fake timers. They MUST cover timeout or cancellation, a late retryable delivery, a later bounded waiter, and a later matching receipt without sleeps, polling assertions, wall-clock deadlines, or real external dependencies.

#### Scenario: Fake-time timeout, late retry, and new waiter

- **WHEN** fake time starts at zero and `input-1` has an active bounded waiter with a 50 ms budget
- **AND** its first delivery remains pending until fake time reaches 50 ms
- **AND** the waiter times out with the first interval's evidence
- **AND** fake time advances to release the late delivery as retryable
- **AND** a new bounded waiter starts for the retained `input-1`
- **THEN** the new waiter MUST have clean evidence
- **AND** a later timeout MUST report only attempts and the latest reason from the new interval
- **AND** the queue snapshot MUST still contain the same `input-1` record until its normal acknowledgement path settles it

#### Scenario: Fake-time cancellation, late retry, and matching settlement

- **WHEN** fake time has an active bounded waiter for `input-2` and its task signal is aborted
- **AND** the leased delivery later returns a retryable response
- **AND** a later retry returns a matching receipt
- **THEN** cancellation MUST end only the local waiter and its evidence
- **AND** the late retry MUST preserve normal record retry behavior without ownerless evidence
- **AND** the matching receipt MUST retire the record without resolving or reopening the cancelled task
