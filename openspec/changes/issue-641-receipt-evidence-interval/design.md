# Design

## Context

The current Runtime Event Queue owns volatile queued records, delivery leases, retry scheduling, and optional input-receipt waiters. Issue #641 added a finite budget and an `AbortSignal` to the ordinary Workflow receipt wait. Attempts and the latest normalized retry reason are held in process memory so a timeout can explain why the budget expired.

A delivery timeout leaves its delivery lease active until the original promise settles. The late completion then applies its ordinary verdict to the retained queue record. That behavior is required to avoid duplicate delivery and to permit a late matching receipt to retire the record. It must not, however, recreate evidence after the waiter that owned that evidence has ended.

## Goals

- Make evidence ownership equal to one active bounded receipt-wait interval.
- Reset attempts, retries, and latest reason when a new bounded waiter begins after a previous interval ended.
- Keep late delivery and queue-record behavior independent from local waiter lifetime.
- Preserve the existing timeout message and its structured attempts, retries, elapsed time, budget, and latest reason.
- Exercise the interval boundary with deterministic fake time.

## Non-Goals

- Restoring the removed durable Runtime Event Outbox.
- Persisting queue records, evidence, intervals, snapshots, or journals.
- Adding a second queue, a receipt store, a delivery cancellation policy, or a global timeout.
- Changing queue capacity, FIFO ordering, delivery leases, retry delays, matching identity, or permanent-refusal handling.
- Changing cleanup-turn admission or the OpenCode/Pi runtime protocols.

## Decisions

### 1. A bounded waiter owns one evidence interval

When a bounded `awaitInputReceipt` call becomes the active waiter for a record, the queue starts a fresh interval with zero attempts, zero derived retries, and no latest reason. Every bounded waiter coalesced onto that active waiter observes the same interval and promise. A later bounded waiter cannot reuse the evidence of an interval that has ended.

The interval ends when the waiter is resolved by a matching receipt, rejected by the existing definitive refusal, rejected by timeout or cancellation, stopped with the queue, or otherwise removed by the queue's existing waiter lifecycle. Ending the interval removes its volatile evidence. The queued record and any delivery lease are separate state and remain governed by the existing queue rules.

An unbounded receipt wait keeps its current behavior. It is not a source of evidence for a later bounded interval, and this change does not add a budget to that caller.

### 2. Evidence is admitted only through an active interval

A delivery attempt and a retryable verdict may update attempts or latest reason only when the record has an active bounded evidence interval. If a delivery settles after timeout or cancellation and no bounded interval is active, the late retryable verdict is applied to the queue record and retry schedule as usual but does not create evidence.

When a new bounded waiter starts while the record is still retained, only delivery work associated with the new active interval contributes to its evidence. A late verdict from the prior interval cannot increment its counters or replace its reason.

### 3. Queue acknowledgement semantics remain authoritative

A matching receipt still requires the current record's existing identity checks. A late matching response may retire a retained record after its waiter has timed out or been cancelled; it must not reopen the completed Workflow task or invoke a Runtime. A permanent refusal or already-consumed result remains the existing `AlreadyConsumedRuntimeEventError`. A non-matching, empty, or retryable transport response remains eligible for the existing retry path.

The local evidence lifecycle therefore does not cancel a delivery lease, remove a queue record, synthesize a receipt, reorder records, or change retry timing.

### 4. Verification uses the queue seam and fake time

Extend the existing Runtime Event Queue focused tests around the injected clock and Vitest fake timers. Use a deferred delivery promise to hold one attempt across waiter expiry, then release a retryable result after expiry. Start a second bounded waiter against the retained record and prove its timeout evidence starts at zero history and contains only the second interval's attempts and reason.

The same matrix covers cancellation, late matching settlement, matching recovery before expiry, coalesced waiters, unrelated records, and existing unbounded callers. Assertions inspect queue snapshots, typed timeout/cancellation errors, delivery calls, and Runtime invocation effects. No wall-clock sleeps, polling loops, or production persistence are permitted.

## Risks and Trade-offs

- A late retryable delivery remains diagnostically invisible to a new waiter if it arrived before that waiter started. This is intentional: evidence describes the active interval, while the retained record remains retryable.
- A delivery that settles exactly at the timeout boundary may race the timer. The queue must use its existing event-loop ordering and settle the waiter at most once; tests must assert outcomes at explicit fake-time boundaries rather than rely on wall-clock ordering.
- Queue state remains process-volatile. Process exit can lose both records and evidence under the current master architecture; this follow-up does not change that contract.

## Migration Plan

1. Change only the existing queue's volatile evidence ownership and lifecycle at the bounded waiter seam.
2. Add deterministic Runner tests for timeout, cancellation, late retryable delivery, new-waiter reset, late matching settlement, and record preservation.
3. Run the focused queue/action tests, `npm run docs:check`, `npm run archtest`, `npm run test:fast`, and `npm run verify`.
4. Confirm the diff contains no Server, persistence, Outbox, journal, schema, or second-queue artifact.

Rollback is a source revert of the queue lifecycle change and its focused tests. No persisted data or protocol migration is introduced.

## Open Questions

None.
