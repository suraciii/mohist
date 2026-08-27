# Design

## Context

The current Runtime Event Queue owns volatile queued records, delivery leases, retry scheduling, and optional input-receipt waiters. Issue #641 added a finite budget and an `AbortSignal` to the ordinary Workflow receipt wait. Attempts and the latest normalized retry reason are held in process memory so a timeout can explain why the budget expired.

A delivery timeout leaves its delivery lease active until the original promise settles. The late completion then applies its ordinary verdict to the retained queue record. That behavior is required to avoid duplicate delivery and to permit a late matching receipt to retire the record. It must not, however, recreate evidence after the waiter that owned that evidence has ended or mutate a newer interval that became active before the old completion.

## Goals

- Make evidence ownership equal to one active bounded receipt-wait interval.
- Reset attempts, retries, and latest reason when a new bounded waiter begins after a previous interval ended.
- Bind every delivery attempt to the interval generation that admitted it, so an old callback cannot write to a newer interval.
- Keep late delivery and queue-record behavior independent from local waiter lifetime.
- Preserve the existing timeout message and its structured attempts, retries, elapsed time, budget, and latest reason.
- Exercise the interval boundary with deterministic fake time and an explicitly deferred delivery.

## Non-Goals

- Restoring the removed durable Runtime Event Outbox.
- Persisting queue records, evidence, intervals, snapshots, or journals.
- Adding a second queue, a receipt store, a delivery cancellation policy, or a global timeout.
- Changing queue capacity, FIFO ordering, delivery leases, retry delays, matching identity, or permanent-refusal handling.
- Changing cleanup-turn admission or the OpenCode/Pi runtime protocols.

## Decisions

### 1. A bounded waiter owns one evidence interval and generation

When a bounded `awaitInputReceipt` call becomes the active waiter for a record, the queue starts a fresh interval with zero attempts, zero derived retries, and no latest reason. The interval receives a unique generation/token owned by that waiter. Every bounded waiter coalesced onto that active waiter observes the same interval and promise. A later bounded waiter cannot reuse the evidence of an interval that has ended.

The interval ends when the waiter is resolved by a matching receipt, rejected by the existing definitive refusal, rejected by timeout or cancellation, stopped with the queue, or otherwise removed by the queue's existing waiter lifecycle. Ending the interval removes its volatile evidence and invalidates its generation/token. The queued record and any delivery lease are separate state and remain governed by the existing queue rules.

An unbounded receipt wait keeps its current behavior. It is not a source of evidence for a later bounded interval, and this change does not add a budget to that caller.

### 2. Attempt ownership is checked at verdict time

When a delivery attempt starts, it captures the currently active interval generation, or captures no generation if no bounded waiter is active. A delivery attempt's attempt counter and retryable verdict may update evidence only if its captured generation still equals the record's active generation at the time of the update.

This token comparison is required even when a new waiter is already active. Thus, if interval `g1` times out, interval `g2` becomes active while `g1`'s leased delivery is still pending, and that old delivery then returns a retryable result, the old result can retain the queue record and schedule its ordinary retry but cannot increment `g2`'s attempts, alter `g2`'s latest reason, or recreate evidence for `g1`. A delivery that started with no generation cannot write evidence even if a bounded waiter starts before that delivery settles.

When a retry attempt starts while `g2` is active, it captures `g2` and may contribute only to `g2`. When no bounded waiter is active, retry behavior continues without evidence. This is current-queue-local ownership only; no persistence is introduced.

### 3. Queue acknowledgement semantics remain authoritative

A matching receipt still requires the current record's existing identity checks. A late matching response may retire a retained record after its waiter has timed out or been cancelled; it must not reopen the completed Workflow task or invoke a Runtime. A permanent refusal or already-consumed result remains the existing `AlreadyConsumedRuntimeEventError`. A non-matching, empty, or retryable transport response remains eligible for the existing retry path.

The local evidence lifecycle therefore does not cancel a delivery lease, remove a queue record, synthesize a receipt, reorder records, or change retry timing.

### 4. Verification uses an exact deferred-delivery sequence

Extend the existing Runtime Event Queue focused tests around the injected clock and Vitest fake timers. The required mutation sequence is:

1. Start bounded waiter `W1` for retained record `input-1` with a 50 ms budget; its first delivery `A1` is deferred and captures generation `g1`.
2. Advance fake time to 50 ms and let `W1` time out; assert `g1` evidence is removed while `A1` remains leased and the queue record remains.
3. **Before resolving `A1`, start bounded waiter `W2` for the same retained record.** Assert `W2` is active under a fresh generation `g2` and has zero attempts and no latest reason.
4. Resolve `A1` with a retryable verdict. Assert the queue retains `input-1`, but `A1` does not increment `g2` or replace its reason.
5. Allow one retry delivery `A2` admitted under `g2` to return a distinct retryable reason. Advance to `W2`'s budget expiry and assert its timeout contains only `A2`'s evidence (exact attempt/retry counts and reason), never `A1`/`g1` evidence.

A companion case MUST leave no bounded waiter active when a late retryable verdict settles and MUST assert that no ownerless evidence is created. The same matrix covers cancellation, late matching settlement, matching recovery before expiry, coalesced waiters, unrelated records, and existing unbounded callers. Assertions inspect queue snapshots, typed timeout/cancellation errors, delivery calls, evidence ownership, and Runtime invocation effects. No wall-clock sleeps, polling loops, or production persistence are permitted.

## Verification Commands

Run these exact commands during implementation:

- `npm run test:run -w packages/runner -- tests/runtime-event-queue.spec.ts`
- `npm run test:run -w packages/runner -- tests/workflow-agent-session-reporter.spec.ts src/actions/pi.test.ts tests/opencode-action-turn.spec.ts`
- `npm run docs:check`
- `npm run archtest`
- `npm run test:fast`
- `npm run verify`

## Risks and Trade-offs

- A late retryable delivery remains diagnostically invisible to a new waiter if it belongs to an old generation or began without an owner. This is intentional: evidence describes the active interval, while the retained record remains eligible for its ordinary retry path.
- A delivery that settles exactly at the timeout boundary may race the timer. The queue must use its existing event-loop ordering and settle the waiter at most once; tests must assert outcomes at explicit fake-time boundaries rather than rely on wall-clock ordering.
- Queue records remain process-volatile. Process exit can lose both records and evidence under the current master architecture; this follow-up does not change that contract.

## Migration Plan

1. Change only the existing queue's volatile evidence ownership and lifecycle at the bounded waiter seam.
2. Capture a generation/token on each delivery attempt and compare it before every evidence mutation.
3. Add deterministic Runner tests for the exact timeout-to-new-waiter-before-late-retry sequence, cancellation, matching settlement, and record preservation.
4. Run the exact focused commands above and the repository gates.
5. Confirm the diff contains no Server, persistence, Outbox, journal, schema, or second-queue artifact.

Rollback is a source revert of the queue lifecycle change and its focused tests. No persisted data or protocol migration is introduced.

## Open Questions

None. A generation/token or equivalent queue-local ownership check is required; persistence is prohibited.
