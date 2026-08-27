## Current-master scope correction

The original Issue #641 described a durable Runtime Event Outbox. Current master deliberately removed that outbox in #780 and uses an in-memory Runtime Event Queue. This port does not restore deleted persistence, journals, snapshots, cleanup waits, or protocol changes. Queue records and delivery evidence remain process-volatile; an undelivered suffix may be lost when the Runner exits.

The observable liveness defect remains while a Runner process lives: a retryable Workflow `session.input` can keep its owning task waiting without a cumulative task-level budget. This change bounds that wait and keeps the queue's existing retry behavior.

## Why

A Workflow Agent task waits for the current queue to obtain a matching `session.input` receipt before invoking OpenCode or Pi. Retryable transport failures, empty responses, and mismatched responses currently leave the queue record eligible for retry but can leave the task waiting indefinitely.

Queue delivery and task liveness are separate facts. The task needs a finite wait budget, while the volatile queue must retain its current record and retry policy until it accepts or drops that record under its existing rules.

## What Changes

- Apply one finite task-level budget to the ordinary Workflow `session.input` receipt wait.
- Keep retryable queue delivery, per-attempt timeout, empty response, and non-matching response handling unchanged while the budget remains.
- Return a typed timeout error with the latest structured reason, elapsed wait, budget, attempts, and retries.
- Return a distinct typed cancellation error when the task signal aborts the wait.
- Project timeout and cancellation through both OpenCode and Pi as `session-reporting-failed` without invoking the runtime.
- Preserve matching acceptance and already-consumed queue outcomes.
- Remove only the local waiter on timeout or cancellation; retain the current queue record and do not change unrelated records.

## Capability

- `workflow-agent-input-receipt-liveness`: bounded live-process receipt waiting for ordinary Workflow Agent input across OpenCode and Pi.

## Impact

- **Runner queue:** `packages/runner/src/server/runtime-event-queue.ts` owns the optional bounded waiter and volatile evidence.
- **Workflow reporting:** `WorkflowAgentSessionReporter` passes the existing task signal and effective OpenCode/Pi turn budget.
- **Action projection:** OpenCode and Pi preserve `session-reporting-failed` while exposing actionable timeout evidence.
- **Server and wire contract:** unchanged. No Server, API, schema, cleanup, artifact, binding, or dependency changes are included.
- **Restart behavior:** unchanged from current master. Queue records and evidence are not durable and may be lost on process death.
