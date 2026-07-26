## Context

The durable `EventDispatcherService` reads persisted CloudEvents, runs every `[Subscription]` handler, retries failures, preserves source ordering, and creates dead letters. `EventBridge` (Web SignalR) and `RunnerWorkflowTerminalStatusHandler` (runner SignalR) are currently subscriptions in that loop. Although their transport code catches most send failures, runner-status routing can still be awaited by durable dispatch, and neither kind of push belongs to the durable domain-reaction channel.

The proposal and `event-ui-push-isolation` spec require push delivery to remain useful without making Web or runner connectivity a workflow dependency. The existing runner status poll endpoint is the recovery mechanism for a missed terminal notification. Architecture also requires background queues to be bounded and to degrade auxiliary work before business work.

## Goals / Non-Goals

**Goals:**
- Keep durable subscriptions exclusively for at-least-once domain reactions and leave their retry, ordering, and dead-letter behavior unchanged.
- Send Web CloudEvent envelopes and runner `Completed` / `Stopped` status notifications through a bounded, best-effort path that cannot wait on SignalR from the durable dispatcher.
- Preserve current SignalR wire shapes, Web subscription filtering, runner assignment lookup, and runner polling convergence.
- Make dropped, timed-out, and failed push delivery observable through structured logs without creating durable-dispatch operational work.

**Non-Goals:**
- Deliver push notifications exactly once, persist a push outbox, replay missed notifications, or change workflow state based on a client acknowledgement.
- Change workflow terminal-event types, runner workspace cleanup policy, public APIs, or the runner polling endpoint.
- Generalize runtime transcript or task-log publishers, which are already direct best-effort paths outside the CloudEvent bus.

## Decisions

### Use a separate bounded event-push worker

Introduce a small event-push queue and hosted worker. `EventDispatcherService` reconstructs each persisted event as it enters its handling lifecycle and performs only a non-blocking enqueue to this queue; it never awaits push handlers. The queue has a fixed configurable capacity and rejects new items when full, recording the drop. The worker invokes independently registered push handlers and contains all handler exceptions and cancellation/timeouts in the push boundary.

The worker receives `TimeProvider` and the configured delivery timeout. It creates each handler's timeout cancellation from that provider and passes the resulting token to the handler; it logs a timeout and continues with the next item. Tests use `FakeTimeProvider` to advance this timeout and await an explicit worker/test signal, never a wall-clock delay or scheduler wait.

This preserves prompt push for events discovered by the existing post-commit dispatcher poke while keeping the durable dispatch gate free of connection lookup and SignalR I/O. The queue is process-local and intentionally has no cursor or persistence; a restart, a full queue, or a failed send can lose a notification.

Alternative considered: retain the current subscriptions and catch exceptions. Rejected because a slow connection still occupies the durable loop and source ordering. Alternative considered: launch one `Task.Run` per event through `IBackgroundTaskLauncher`. Rejected because that launcher is unbounded and a connection outage could consume unbounded resources. Alternative considered: persist a second push outbox. Rejected because it adds delivery state and retry semantics to an explicitly best-effort channel.

### Separate push-handler registration from durable subscriptions

Add an explicit push-handler interface and registration path, separate from `[Subscription]` discovery. Move `EventBridge` to this path, retaining its event filtering, target resolution, and `OnEvent` envelope behavior. Replace `RunnerWorkflowTerminalStatusHandler` with a push handler that retains its terminal-event type resolution and delegates to `IRunnerWorkflowStatusRouter`.

The durable handler list will no longer include either push handler. Consequently, neither can appear in a durable dead letter or be selected by dead-letter redelivery. Durable handlers such as workflow-to-Issue and lock-release reactions remain attribute-discovered and unchanged.

Alternative considered: add a delivery-mode flag to `SubscriptionAttribute` and have one dispatcher branch on it. Rejected because it keeps the two reliability contracts coupled to the durable subscription model and makes accidental use of durable state for push easier.

### Preserve terminal-status and convergence semantics

The runner push handler accepts only `com.mohist.workflow.run.completed` and `com.mohist.workflow.run.stopped`, extracts the existing workflow-run lineage extension, and calls the existing router. It does not route `Failed`: failed runs can be retried or rerun and therefore must retain their workspace. The router retains its current assigned-runner lookup, connection check, payload (`ReceiveWorkflowRunStatus` with `WorkflowRunStatusNotification`), and best-effort logging.

No retry is attempted for a dropped queue item, missing runner connection, timeout, or SignalR error. Runner polling remains authoritative and makes a missed completed/stopped notification converge after reconnect.

Alternative considered: notify runners for `Failed` as well. Rejected because it can make a recoverable workspace cleanup-eligible before a retry or rerun.

### Test the reliability boundary directly

Replace the terminal-status handler test that expects router exceptions to reach durable dispatch. Add focused tests for non-blocking queue admission, full-queue drop, worker exception containment, and the absence of push handlers from the durable subscription collection. Drive handler timeout coverage with `FakeTimeProvider` and an awaitable worker signal. Retain and adapt Web envelope fan-out and runner router tests. Add dispatcher-level coverage showing a failed push neither holds source ordering nor creates a dead letter, while a failing durable handler retains its existing retry behavior.

## Risks / Trade-offs

- [A full queue or process restart drops a fresh notification] -> The queue is intentionally best-effort; Web clients re-fetch authoritative state and runners reconcile through status polling.
- [A push can be duplicated across dispatcher retries or process restart] -> Consumers treat push as an invalidation/status hint, and authoritative reads remain idempotent.
- [A slow handler reduces push throughput] -> Per-delivery cancellation is driven by injected `TimeProvider`, and bounded capacity caps resource consumption; drops are logged for operational visibility.
- [A future push handler is accidentally registered as durable] -> Keep separate interfaces/registration and add an architecture or registration test that asserts the durable collection excludes push handlers.

## Migration Plan

1. Add the bounded queue, hosted worker, push-handler registration, and DI wiring.
2. Convert `EventBridge` and terminal runner status delivery to push handlers; remove their `[Subscription]` registrations and obsolete durable-handler tests/dead-letter fixtures.
3. Add the focused tests described above, then deploy without schema or API migration.
4. During rollout, duplicate or missed pushes are acceptable; verify runner polling continues to converge and inspect push-drop/error logs.

Rollback is a binary rollback only. Persisted workflow events and durable reaction state remain compatible, and runners continue to reconcile through polling; no data migration or cleanup is required.

## Open Questions

None. Queue capacity and delivery timeout will be server configuration with conservative defaults and are operational tuning values, not a new product contract.
