## Why

The durable event dispatcher currently waits for runner status SignalR delivery after a workflow reaches a terminal state, so an unavailable UI connection can consume retry capacity, delay later events from the same source, or create a dead letter for a notification that the runner can reconcile independently. This conflicts with the event-channel boundary: workflow progression needs durable domain reactions, while UI and runner push is best-effort.

## What Changes

- Move workflow terminal-status push delivery out of the durable event-dispatch loop into a best-effort notification path.
- Preserve terminal workflow events and durable domain subscriptions so workflow state transitions and cross-aggregate reactions retain their existing at-least-once delivery behavior.
- Treat unavailable connections, push failures, and notification timeouts as logged, non-retriable notification outcomes; they must not block source ordering or produce durable-dispatch dead letters.
- Preserve runner-side status reconciliation so a runner that misses a terminal push still converges on the authoritative workflow status.

## Capabilities

- `event-ui-push-isolation`: UI and runner lifecycle push notifications are best-effort side effects outside durable domain-event delivery; failed or missed pushes do not affect workflow events, while reconnecting or polling clients reconcile from authoritative state.

## Impact

- **Server:** event dispatch and subscription registration, terminal workflow-status notification routing, and SignalR notification delivery for runners and Web clients.
- **Tests:** replace durable-handler retry expectations for terminal status push with coverage that notification failure cannot block, retry, or dead-letter workflow events, and that reconciliation remains available.
- **APIs and dependencies:** no public API, CLI, persistence-schema, or external dependency changes; the existing workflow status notification payload and runner reconciliation endpoint remain compatible.
