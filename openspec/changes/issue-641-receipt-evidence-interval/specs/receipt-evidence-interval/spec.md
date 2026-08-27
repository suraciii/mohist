# Receipt Evidence Interval

## Requirements

### Requirement: Bounded receipt evidence belongs to one active interval

The Runtime Event Queue MUST associate attempts, retries, and the latest normalized retry reason with the active bounded waiter interval for one receipt-bearing record. When a bounded waiter begins after no bounded interval is active, the queue MUST create a new interval with empty evidence and a new generation/token. Evidence MUST be removed when that interval ends.

#### Scenario: A new waiter starts with clean evidence

- **WHEN** a bounded waiter for record `input-1` has already ended through timeout or cancellation
- **AND** the queue record remains retained
- **THEN** a later bounded waiter for `input-1` MUST start with a new generation/token, zero prior attempts, and no prior latest reason
- **AND** a timeout from the later waiter MUST report only evidence observed during the later interval

#### Scenario: Coalesced waiters share the active interval

- **WHEN** two bounded callers wait for the same retained record before its active interval ends
- **THEN** both callers MUST observe the existing coalesced waiter outcome
- **AND** their timeout evidence MUST describe one shared interval and generation rather than two independently counted delivery histories

### Requirement: Delivery evidence is generation-owned and has no ownerless interval

A delivery attempt MUST capture the active bounded waiter's generation/token when it starts, or no token when no bounded waiter is active. An attempt or retryable verdict MUST update receipt evidence only when its captured token still owns the record's active interval. A retryable delivery that settles with no bounded waiter active MUST NOT create evidence. A late verdict from an old generation MUST NOT update a newer interval that became active before the old delivery completed.

#### Scenario: Late retryable delivery cannot recreate ownerless evidence

- **WHEN** a bounded waiter for `input-1` times out while delivery attempt one is still leased
- **AND** no bounded waiter is active when that late delivery settles with a retryable transport failure
- **THEN** the queue MUST retain `input-1` for its existing retry path
- **AND** the late verdict MUST NOT recreate attempts, retries, or latest-reason evidence for `input-1`

#### Scenario: New waiter is active before the old retryable verdict completes

- **WHEN** bounded waiter `W1` for `input-1` times out and its leased delivery `A1` remains pending under generation `g1`
- **AND** bounded waiter `W2` starts for retained `input-1` under a fresh generation `g2` **before `A1` resolves**
- **AND** `W2` is active with zero attempts and no latest reason when `A1` resolves retryably
- **THEN** `A1` MUST retain the queue record and existing retry path
- **AND** `A1` MUST NOT increment `g2`'s attempts or retries
- **AND** `A1` MUST NOT replace `g2`'s latest reason
- **AND** only a retry attempt captured under `g2` MAY contribute to `W2`'s evidence

#### Scenario: A delivery with no token cannot write to a later interval

- **WHEN** a retry delivery starts while no bounded waiter is active and captures no generation/token
- **AND** a bounded waiter starts before that delivery settles
- **THEN** the no-token verdict MUST NOT create evidence for the later interval
- **AND** the later interval MUST retain its own clean evidence until a delivery admitted under its token occurs

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
- **AND** it MUST update the active interval's latest reason only when the attempt token owns that interval
- **AND** it MUST not extend the bounded wait beyond its original budget

### Requirement: Evidence is volatile and has no new persistence boundary

Receipt evidence MUST remain process-local and MUST be discarded when its active interval ends or the queue stops. This capability MUST NOT add persistence, an Outbox, a journal, a snapshot, a receipt store, a second queue, or a new Server/API/schema contract.

#### Scenario: Queue stop discards interval evidence

- **WHEN** the queue stops while a bounded receipt waiter is active
- **THEN** the waiter MUST end through the existing queue-stop behavior
- **AND** its local evidence MUST be discarded
- **AND** no evidence MUST be recoverable from a new process

### Requirement: Interval boundaries are verified with deterministic time and ordering

Focused tests for this capability MUST use the queue's injectable time seam and fake timers. They MUST exercise the required ordering in which a new bounded waiter becomes active before the previous late retryable delivery completes. They MUST prove the old interval/token cannot create or mutate the new interval's attempts or reason, and that a retryable verdict with no bounded waiter creates no ownerless evidence. Tests MUST cover timeout or cancellation, late retryable delivery, a later bounded waiter, and a later matching receipt without sleeps, polling assertions, wall-clock deadlines, or real external dependencies.

#### Scenario: Fake-time timeout, new waiter, late retry, and new-interval timeout

- **WHEN** fake time starts at zero and `input-1` has bounded waiter `W1` with a 50 ms budget
- **AND** its first delivery `A1` remains pending under generation `g1` until after fake time reaches 50 ms
- **AND** `W1` times out and removes `g1` evidence while `input-1` remains retained
- **AND** **before resolving `A1`**, bounded waiter `W2` starts under fresh generation `g2`
- **AND** `W2` is observed active with zero attempts and no latest reason
- **AND** `A1` then resolves with a retryable verdict
- **THEN** `A1` MUST NOT alter `g2`'s attempts, retries, or latest reason
- **AND** a retry delivery captured under `g2` MUST be the only old-independent evidence counted by `W2`
- **AND** a later timeout MUST report only the `g2` attempt/reason values
- **AND** the queue snapshot MUST still contain the same `input-1` record until its normal acknowledgement path settles it

#### Scenario: Fake-time cancellation, late retry, and matching settlement

- **WHEN** fake time has an active bounded waiter for `input-2` and its task signal is aborted
- **AND** its leased delivery later returns a retryable response with no bounded waiter owning the old token
- **AND** a later retry returns a matching receipt
- **THEN** cancellation MUST end only the local waiter and its evidence
- **AND** the late retry MUST preserve normal record retry behavior without ownerless evidence or mutation of a later interval
- **AND** the matching receipt MUST retire the record without resolving or reopening the cancelled task
