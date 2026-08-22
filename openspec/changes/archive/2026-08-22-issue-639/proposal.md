## Why

The Runner's durable runtime-event outbox can retain records that the Server will deterministically refuse or has already consumed, so those records retry forever instead of converging. The resulting backlog—currently thousands of reconnect activity records and stale input records—causes repeated large snapshot writes, warning floods, and competition with live input receipts, delaying Workflow stage transitions; this must be corrected before the backlog becomes a recurring operational failure.

## What Changes

- Accept reconnect activity observations submitted through the session runtime-events route when the reported runtime binding is current and the batch contains only `session.activity` events, even when no Agent turn identity is available. These observations remain session-level facts and MUST NOT create workflow-attributed observations.
- Preserve the fail-closed workflow attribution contract: non-activity or workflow-observation events on workflow-introduced sessions still require the complete acknowledged Agent turn binding, and stale or mismatched bindings remain rejected or ignored according to the existing contract.
- Add bounded terminal settlement for `binding-reconcile` records after repeated deterministic 4xx refusals for the same delivery key. The blocked records are removed from the pending queue and one actionable error is logged; transient 5xx responses and transport failures continue to retain and retry records.
- Allow `matching-receipt` input and cleanup records to settle as already consumed after two consecutive valid 2xx responses with empty receipt arrays. Positive matching receipts retain their current identity checks, while transient failures and unconfirmed empty responses continue to retry.
- Change retention-cap observability to emit one warning per cap crossing rather than one warning for every enqueue while the queue remains over the cap. Preserve the existing retention policy and avoid a snapshot-format redesign in this change.
- Ensure a saturated historical backlog can converge without indefinitely blocking live Workflow input-receipt waits or stage transitions, with deterministic timer-driven coverage for the settlement and retry boundaries.

## Capabilities

- `session-runtime-activity-reconciliation`: Acceptance and classification of reconnect `session.activity` observations for current AgentSession bindings, including the boundary between session-level activity and workflow-attributed runtime events.
- `runtime-event-outbox-convergence`: Durable outbox settlement for deterministic refusals and confirmed-consumed inputs, retry behavior for transient failures, retention-cap warning hygiene, and liveness of live Workflow delivery while historical records drain.

## Impact

- **Server runtime-event boundary:** The Runner session runtime-events route and `AgentSessionGrain.AppendRuntimeEventsAsync` binding/observation rules under `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs` and `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs` will change. Existing workflow turn attribution and stale-binding protections remain in force.
- **Runner delivery:** `packages/runner/src/server/runtime-event-outbox.ts` and its delivery/identity integration will gain terminal handling for deterministic 4xx responses and repeated empty receipts, plus warn-once retention tracking. Durable records remain in the existing snapshot file and are not retargeted or blindly replayed.
- **Workflow liveness:** Runtime-event receipt waiters, reconnect binding convergence, Runner scheduling fairness, and Workflow stage-transition latency are affected. Existing live and recovered outbox records must converge without manual deletion.
- **Verification and dependencies:** Server integration/spec tests and Runner fake-timer outbox tests will cover accepted activity-only appends, preserved rejection of unattributed workflow events, dead-letter and already-consumed settlement, transient retry behavior, warning crossings, and saturated-queue progress. No new external dependency or snapshot schema migration is expected.
