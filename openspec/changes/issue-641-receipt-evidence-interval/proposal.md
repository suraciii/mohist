# Queue Receipt Evidence Interval

## Why

Issue #641 bounded ordinary Workflow `session.input` waiting in the current in-memory Runtime Event Queue. The queue correctly removes timeout and cancellation waiters, but a delivery that was already in flight can finish later. Its retryable verdict currently has no active waiter to own it, yet it can recreate evidence for the record. A later waiter can then report attempts and a reason from an earlier, already-finished wait interval.

That evidence is diagnostic context for one bounded waiter. It is not a durable receipt, a second queue, or a property of the queued record itself. The lifecycle must therefore be explicit across the interval boundary, including the case where a new waiter becomes active while the previous delivery is still pending.

## What Changes

- Give each bounded input-receipt wait its own evidence interval and generation/token.
- Start a new bounded interval with empty attempts, retries, and latest-reason state.
- Associate each delivery attempt with the interval generation active when that attempt began, or with no generation when no bounded waiter owned it.
- Update evidence only when the attempt's generation still owns the active interval. A late retryable verdict from an old generation MUST be ignored by a newer interval, and a verdict with no generation MUST never create ownerless evidence.
- Require deterministic coverage where a new waiter is active before the prior late retryable delivery completes; prove the old verdict cannot increment the new interval's attempts or replace its reason.
- Preserve matching acknowledgement, permanent refusal, delivery leases, FIFO ordering, retry scheduling, and late matching settlement.
- Verify timeout, cancellation, late retryable delivery, and a later waiter with fake time, without sleeps, polling, persistence, an Outbox, a journal, or a second queue.

## Capability

- `receipt-evidence-interval`: lifecycle ownership for volatile diagnostic evidence attached to one active bounded Runtime Event Queue receipt wait.

## Impact

- **Runner queue:** the existing in-memory input-receipt waiter and evidence bookkeeping define the generation/token lifecycle.
- **Action projection:** timeout evidence remains available to the existing OpenCode and Pi `session-reporting-failed` projection; no action contract or runtime protocol changes.
- **Queue records:** records remain process-volatile and continue through their current delivery and retry paths after a local waiter ends.
- **Persistence and services:** no Server, API, schema, database, Outbox, journal, snapshot, receipt store, or second queue changes are included.
