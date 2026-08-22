## Context

Issue 639 is caused by two independent delivery paths that currently treat terminal conditions as retryable:

- `binding-reconcile` records report reconnect activity without a Workflow turn identity. Workflow-introduced sessions currently reject that request whenever the runtime has a Workflow-bound turn, so the same records retry forever.
- `matching-receipt` records retain `session.input` and `session.cleanup` when the Server has already consumed them and a replay returns an empty receipt array.

The backlog is durable in Runner snapshot version 1 and is amplified by full-snapshot writes and retention-cap warning floods. The Server-side binding fence and Workflow attribution contract must remain fail-closed. The main implementation areas are:

- `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs`
- `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs`
- `packages/runner/src/server/connection.ts` and runtime-event delivery error handling
- `packages/runner/src/server/runtime-event-outbox.ts`
- existing Server integration/spec tests and Runner fake-timer outbox tests

No new external dependency or snapshot-format migration is required.

## Goals / Non-Goals

**Goals:**

- Accept current-binding, activity-only session observations without Agent turn identity as session-level facts.
- Preserve complete Workflow turn attribution and stale-binding rejection for Workflow runtime events.
- Bound retries for deterministic `binding-reconcile` 4xx refusals, removing all pending records for the affected delivery key in one durable settlement.
- Confirm already-consumed `matching-receipt` records after two consecutive valid 2xx responses with empty receipts, without inventing an Agent turn or receipt identity.
- Ensure retryable failures remain durable and continue using existing retry timing.
- Change retention-cap logging to one warning per over-cap crossing while preserving the current eviction policy and snapshot representation.
- Preserve the existing round-robin, bounded-concurrency scheduler so independent live groups continue to make progress while historical groups retry.

**Non-Goals:**

- Redesigning the snapshot format, adding a separate dead-letter file, or introducing operator retargeting/deletion commands.
- Relaxing Workflow route validation or inferring Workflow attribution from `session.activity`.
- Dropping non-delta records through retention eviction.
- Changing Server activity semantics or creating new Workflow observations for reconnect activity.
- Making transient 5xx, timeout, transport, malformed-response, or unconfirmed-empty outcomes terminal.

## Decisions

1. **Keep the activity relaxation in the grain, behind the existing session route.**

   `AgentSessionGrain.AppendRuntimeEventsAsync` will classify the persisted session before deciding whether an unattributed request may proceed. A session is Workflow-introduced only when `session.Metadata.Label(AgentSessionQueryMetadataKeys.SourceKind)` equals `"workflow"`; this is the authoritative classification and does not depend on whether the current runtime already has a persisted Workflow turn. For a Workflow-introduced session, a request with neither `SessionTurnId` nor `WorkflowExecution` is eligible for the relaxation only when it is a non-empty pure batch whose every event is `session.activity`. Any unattributed non-activity event, including a mixed activity/non-activity batch, is rejected before `AppendEventsAsync` and therefore cannot partially append.

   A pure activity request then proceeds to `AppendEventsAsync`, which enforces the current `runtimeSessionId` fence. This permits reconnect activity when the current runtime has no persisted turn and also preserves the same stale/missing-binding no-op or rejection behavior. The existing `hasWorkflowTurnForRuntime` check remains an attribution check for non-activity requests; it is not the boundary for deciding whether a Workflow-introduced session may submit an unattributed batch. Because the accepted command has no `WorkflowExecution`, `BuildWorkflowRuntimeObservations` is not entered and the event updates session/transcript state only.

   The session-scoped route continues to require `runtimeSessionId` and delegates the source-kind and pure-batch rule to the grain. The Workflow-labeled route remains unchanged and still requires the complete acknowledged execution binding, including Agent turn, TaskRun, Work, runtime, and current runtime session identities.

   *Alternative considered:* special-casing this only in `RunnerRoutes.cs`. That would duplicate domain rules and could allow another caller to bypass the grain-level attribution fence. Keeping the rule in the grain makes the append and observation behavior atomic and authoritative.

2. **Represent HTTP refusal metadata explicitly rather than parsing error strings.**

   Runtime-event connection methods will preserve response status and the top-level Server `ApiResponse.Code` (when present) in a typed HTTP error. The outbox will classify only the following structured combinations for `binding-reconcile` records:

   - HTTP 409 with code `conflict` (the current session runtime-events middleware response), `agent_session_changed`, `workflow_agent_session_changed`, `workflow_runtime_binding_rejected`, or `workflow_cleanup_binding_rejected`.
   - HTTP 400 with code `validation`, `runtime_session_id_required`, `session_runtime_identity_required`, `session_runtime_task_identity_invalid`, or `workflow_runtime_binding_required`.

   The 409 `conflict` combination is deliberately included because it is the observed refusal shape from the session runtime-events route. No other 4xx code is terminal: unknown 400/409 codes, 401/403 authentication/authorization failures, and 404s remain durable and retryable. HTTP 408 and 429 are explicitly retryable, as are all 5xx responses, timeouts, aborts, malformed responses, and transport failures. The predicate is an allowlist over `(status, code)`, not a status-only rule and never parses exception text.

   *Alternative considered:* change `RuntimeEventDelivery` to return a status-bearing result for every response. That would make successful delivery more complex and require changes to all delivery implementations. Structured errors preserve the current successful-return contract while exposing metadata only on failures.

3. **Use an in-memory consecutive-refusal counter keyed by `runtimeEventDeliveryKey`.**

   The outbox will maintain a map from binding delivery key to consecutive deterministic 4xx count. A small fixed threshold (three refusals) bounds the retry window without adding runtime configuration. A successful response, retryable failure, or any non-deterministic outcome resets that key's counter.

   When the threshold is reached, the outbox will remove every currently pending record whose delivery key matches the refused binding key, persist the removal through the existing serialized snapshot-write tail, and emit exactly one actionable error after persistence succeeds. The settlement is one operation even if the refusal was returned for a batch; newly discovered records for the same key are not retargeted to another runtime session.

   Counters are intentionally not added to snapshot version 1. After restart, a recovered record begins a new consecutive window but follows the same refusal and retry rules. This avoids a snapshot migration while still guaranteeing convergence after a bounded number of post-start refusals.

   *Alternative considered:* retain records in a separate dead-letter queue. That would preserve more diagnostic data but add a new durable format and operator lifecycle for facts that are already known to be unusable. The issue requires removal from the pending queue and explicitly excludes a format redesign.

4. **Treat two consecutive empty 2xx responses as a typed terminal already-consumed result.**

   For `session.input` and Workflow cleanup `session.cleanup`, the outbox will keep a per-record empty-confirmation count. The first valid empty receipt array leaves the record durable and does not release the waiter. A second consecutive valid empty response removes and persists the record as already consumed.

   Cleanup must use the same receipt-array protocol as the ordinary runtime-events routes. `RunnerRoutes.WorkflowCleanup` will return a 2xx JSON receipt array: a newly persisted cleanup operation returns one `session.cleanup` receipt with the expected operation-derived input-delivery id, AgentSession id, and non-empty Agent turn id; an idempotent replay of an already persisted cleanup operation returns `[]` after rechecking the complete request identity. The grain result will expose whether the operation was newly recorded or already present so the route does not fabricate a positive receipt on replay. `ServerConnection.workflowAgentSessionCleanupTurn` and `runtime-event-delivery.ts` will pass through either the one-element array or the empty array unchanged. Conflict/validation responses retain their existing structured status and code behavior.

   Any positive response, non-matching receipt, malformed response, non-2xx response, timeout, or transport failure resets the empty-confirmation count. Positive receipts still pass the existing event-type, input-delivery, AgentSession, and non-empty Agent turn checks before settlement; the exact cleanup identity checks apply to `session.cleanup` as well.

   The waiter API will retain its successful receipt shape and report already-consumed settlement through a typed terminal error/outcome carrying the record id and `already-consumed` classification. It will not provide a fabricated receipt, Agent turn, or identity. Workflow reporter callers will handle this outcome explicitly and stop the attributed turn path rather than setting `agentTurnId` from incomplete data; cleanup callers must also avoid enqueueing the follow-on runtime `session.input` after this outcome. Retryable outcomes leave a waiter pending (subject to the existing delivery timeout behavior), so one empty response cannot prematurely fail the action.

   Settlement processing will stage record removals and their outcomes, persist the new snapshot, then resolve/reject waiters. If persistence fails, records and confirmation state remain retryable and no terminal outcome is exposed.

   *Alternative considered:* return a synthetic receipt so existing callers need no changes. That would violate the fail-closed identity contract and could cause later runtime events to be attributed to a turn that was never identified. A union return type was also considered; a typed terminal error preserves the existing positive `Promise<Receipt>` API and makes terminal non-success explicit.

5. **Make retention warnings edge-triggered in memory.**

   Add a boolean over-cap state to the outbox. After retention enforcement and after removals, compare the current retained count with the configured cap. Emit a warning only on a transition from at-or-below to over-cap, and clear the state when the queue returns to at-or-below. Loading an already-over-cap recovered snapshot initializes the state and emits at most one warning for that loaded over-cap interval.

   The existing policy remains unchanged: streaming delta records are evicted first; non-delta facts are retained; version-1 snapshots remain the durable representation.

   *Alternative considered:* rate-limit warnings by elapsed time. Time-based suppression would still produce duplicate warnings during a single sustained incident and would make fake-timer tests less deterministic. Tracking the state transition directly matches the requirement.

6. **Preserve the current fair scheduler rather than adding a second queue.**

   `collectGroups`, `selectDeliveryGroups`, `nextDeliveryGroupLabel`, and bounded concurrency already provide independent-group round-robin opportunities. The convergence change will make terminal settlements use the same group lease and retry path, while the scheduler tests will add a saturated mixture of historical refusal/empty-confirmation groups and a live Workflow input group.

   A binding refusal must not hold the lease indefinitely: below threshold it releases normally and schedules the existing retry; at threshold it settles all records for that key and releases the group. A live group can therefore receive a bounded-concurrency slot while historical groups are waiting on retry timers.

   *Alternative considered:* prioritize live Workflow input records globally. That would require new priority semantics and could violate existing FIFO guarantees. Independent-group fairness is sufficient for the specified liveness boundary and keeps the scheduling model small.

## Risks / Trade-offs

- **[A deterministic 4xx classification may discard an observation that could succeed after an operator or deployment change.]** -> Restrict terminal candidates to structured, known non-retryable runtime-event refusals, keep the threshold at three, log the delivery key/status/count, and preserve the durable snapshot backup/rollback procedure.
- **[Counters reset on Runner restart, so a bad recovered key can make several retries again.]** -> Counters are bounded per process and recovery uses the same rules; fair scheduling prevents one group from monopolizing live delivery. Persisting counters would require a snapshot schema change that is out of scope.
- **[Already-consumed settlement gives no Agent turn identity to the active caller.]** -> Expose a typed terminal outcome and keep the caller fail-closed; never synthesize identity. The workflow action must stop or escalate rather than emit attributed events without a turn.
- **[A large non-delta backlog can still require full snapshot serialization on each durable settlement.]** -> Remove all records for a terminal binding key in one write, batch normal delivery where already supported, and retain bounded concurrency. Snapshot compaction/redesign is explicitly deferred.
- **[A stale activity request can receive HTTP 2xx with an empty event result.]** -> The current-binding check remains inside `AppendEventsAsync`; the outbox's `successful-response` policy treats the valid response as settled without claiming that the stale event was appended.
- **[Deploying the Runner before the Server change can consume the refusal budget for activity facts.]** -> Roll out the Server behavior first, then restart/update Runners, and monitor terminal-settlement and retention-crossing logs.

## Migration Plan

1. Add Server grain/spec coverage for current-binding activity-only appends, mixed/non-activity unattributed rejection, stale binding no-op behavior, and preservation of Workflow route fail-closed validation.
2. Add structured runtime-event HTTP error metadata and Runner fake-timer coverage for three-key refusal settlement, transient retry preservation, two-empty confirmation, positive receipt identity checks, warning crossings, recovered snapshots, and saturated-group liveness.
3. Deploy the Server change first. It is backward-compatible with the existing Runner and does not alter the snapshot format.
4. Deploy/restart the Runner. Existing version-1 snapshots load unchanged. Current activity records should settle through successful session-level appends; stale matching records require two valid empty confirmations; deterministic refused records are removed after the threshold.
5. Monitor the Runner log for one terminal-settlement error per refused key, retention-cap crossing warnings, and any continuing retryable failures. Verify that live input receipt waits and Workflow stage transitions proceed while historical records drain.

Rollback is code-compatible because no snapshot version changes. Stop the affected Runner and preserve a copy of its snapshot before restoring an older binary. Restoring the copied snapshot can intentionally reintroduce records that were already consumed or terminally refused, so rollback should be followed by either redeployment of the fix or explicit operational cleanup; the design does not add automatic resurrection of settled records.

## Open Questions

- Confirm the desired higher-level Workflow behavior when an active waiter receives the typed `already-consumed` outcome: fail the current turn, trigger a fresh input/reconciliation, or use another existing recovery path. The outbox itself will remain fail-closed and will not invent an Agent turn.
- Decide whether terminal-settlement counts should receive a metric in addition to the required actionable log. This is observability-only and does not block the implementation.
