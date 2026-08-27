# Queue Receipt Evidence Interval

## Why

Issue #641 bounded ordinary Workflow `session.input` waiting in the current in-memory Runtime Event Queue. The queue correctly removes timeout and cancellation waiters, but a delivery that was already in flight can finish later. Its retryable verdict currently has no active waiter to own it, yet it can recreate evidence for the record. A later waiter can then report attempts and a reason from an earlier, already-finished wait interval.

That evidence is diagnostic context for one bounded waiter. It is not a durable receipt, a second queue, or a property of the queued record itself. The lifecycle must therefore be explicit before another task relies on timeout evidence.

## What Changes

- Give each bounded input-receipt wait its own evidence interval.
- Start a new bounded interval with empty attempts, retries, and latest-reason state.
- Update evidence only while that interval has an active owner.
- Ignore retryable outcomes from late deliveries when no bounded waiter is active; keep the queued record and its existing retry behavior unchanged.
- Prevent a later waiter from inheriting evidence from a timed-out, cancelled, or otherwise completed interval.
- Preserve matching acknowledgement, permanent refusal, delivery leases, FIFO ordering, retry scheduling, and late matching settlement.
- Verify timeout, late retryable delivery, and a later waiter with fake time, without sleeps, polling, persistence, an Outbox, a journal, or a second queue.

## Capability

- `receipt-evidence-interval`: lifecycle ownership for volatile diagnostic evidence attached to one active bounded Runtime Event Queue receipt wait.

## Impact

- **Runner queue:** the existing in-memory input-receipt waiter and evidence bookkeeping define the lifecycle.
- **Action projection:** timeout evidence remains available to the existing OpenCode and Pi `session-reporting-failed` projection; no action contract or runtime protocol changes.
- **Queue records:** records remain process-volatile and continue through their current delivery and retry paths after a local waiter ends.
- **Persistence and services:** no Server, API, schema, database, Outbox, journal, snapshot, or second queue changes are included.
