## Context

Current master uses `AgentSessionRuntimeEventQueue`, an in-memory queue introduced by #780. It retries Runtime Event delivery from process memory, enforces per-session ordering and queue capacities, and drops records on permanent refusal or process exit. It has no durable snapshot, replay journal, or persisted delivery evidence.

An ordinary Workflow Agent turn enqueues `session.input`, waits for a matching receipt, and only then invokes OpenCode or Pi. The current queue keeps retryable records eligible for retry, but its receipt waiter has no cumulative task budget. The port is therefore limited to live-process task liveness and does not revive the deleted durable Outbox.

## Goals

- Bound ordinary Workflow `session.input` waiting by one fixed budget.
- Keep existing queue retries, delivery timeouts, ordering, capacity, and definitive refusal behavior.
- Preserve the latest retry reason and attempt/retry evidence in the local timeout error.
- Ensure OpenCode and Pi never invoke their runtime after receipt-budget exhaustion or task cancellation.
- Leave the queued input and unrelated queued records unchanged when only the waiter ends.

## Non-goals

- Restoring a durable Runtime Event Outbox, snapshot, journal, cleanup wait, or recovery store.
- Changing Runtime Event endpoints, receipt identities, Server behavior, schemas, or wire protocols.
- Changing cleanup-turn admission, artifact upload, binding settlement, task-log delivery, or provider-specific handling.
- Adding a second queue, polling loop, or global timeout policy.

## Decisions

### 1. The queue owns the bounded waiter

Extend `awaitInputReceipt(recordId)` with an optional `{ budgetMs, signal }` argument. The no-options form keeps its current behavior for follow-up callers and tests that do not opt into a task budget.

For a bounded waiter, the queue records its start time, arms one local timer, and listens to the caller's `AbortSignal`. Retryable delivery outcomes leave the waiter registered. Timeout or cancellation removes only that waiter and rejects it; it does not remove the queue record, cancel a delivery lease, alter retry scheduling, or touch another record.

The queue accepts an injected `now` function for scheduling and elapsed-time evidence. Tests use Vitest fake timers to drive the real timers without sleeps or polling.

### 2. Evidence is volatile and attached to the current queue interval

At delivery start, increment attempts for receipt-bearing records. For retryable transport errors, delivery timeouts, empty responses, and non-matching responses, update the latest normalized reason. Retries are `max(0, attempts - 1)`.

`InputReceiptWaitTimeoutError` snapshots this evidence and reports the record id, elapsed milliseconds, budget, attempts, retries, and last reason. `InputReceiptWaitCancelledError` remains distinguishable and includes the task cancellation reason when available. Evidence is deleted when the queue record settles or its local waiter ends; it is never written to disk because current master has no runtime-event persistence.

Matching receipts still retire the record before resolving the waiter. Permanent refusal still rejects the waiter as `AlreadyConsumedRuntimeEventError`. A mismatched positive response remains retryable and cannot authorize Runtime execution.

### 3. Existing action budgets and signals are the only task boundary

The OpenCode executor passes its effective `deadlineMs` and task signal to the ordinary Workflow reporter. Pi passes its effective `timeoutMs` and task signal. Cleanup reporters intentionally omit the ordinary-input budget.

When the bounded waiter rejects with timeout or cancellation, the OpenCode and Pi action boundaries return `session-reporting-failed` with the typed message and do not invoke a Runtime. Other enqueue failures retain their existing `execution-unavailable` or reporting behavior.

### 4. Late delivery remains ordinary queue behavior

After local timeout or cancellation, a late matching response may still retire the retained queue record because the queue owns delivery independently of the task waiter. The completed task cannot be reopened, and no Runtime continuation is attached to that late response. This preserves current delivery-lease and ordering semantics without adding a receipt store.

## Verification

Queue tests cover continuous retry through expiry, structured reason formatting, unchanged input and unrelated records, cancellation, matching recovery, coalesced waiters, and late settlement. Pi and OpenCode action tests cover `session-reporting-failed` projection without Runtime invocation and verify exact budget/signal wiring. Existing matching, permanent refusal, queue capacity, ordering, and delivery-timeout tests remain regression coverage.
