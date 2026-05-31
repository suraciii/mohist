## Context

Mohist workflow dispatch relies on `WorkflowLease` as the durable single-owner guard for an active workflow work item. Today `WorkflowGrain.OnActivateAsync` loads a persisted lease, copies the runner id, and then clears the in-memory lease reference. After grain reactivation or server recovery, `GetWorkAsync` can therefore treat the workflow as dispatchable even though durable storage says another runner still owns the same work item.

This breaks the user-facing invariant that one workflow task has one active executor. It also lets read models diverge: `WorkflowLeases` may name one runner, while `WorkflowAgentSessions` and `/api/agent/activity` still show another runner as active for the same work item. Event history can then contain two `workflow_task_started` events for the same workflow run and task without an intervening abandonment, timeout, retry, or handoff.

Backlog recovery has a related correctness issue. Some workflow rows do not have project identity in metadata annotations or indexed `MetadataProjectId`, while the durable workflow variables still contain the real project and issue identity. Recovery currently falls back to the `default` backlog, which can create incorrect project-scoped claims and make active workflows visible in the wrong queue.

The affected stakeholders are users relying on safe autonomous execution, runners polling for work, workflow/event consumers, and API/UI readers that display active agent ownership.

## Goals / Non-Goals

**Goals:**

- Preserve persisted active `WorkflowLease` state across grain activation.
- Make `GetWorkAsync` refuse duplicate dispatch when a valid active lease already exists.
- Reconcile stale leases through the same explicit abandonment, timeout, failure, retry, or handoff paths used by runner lifecycle recovery before redispatch.
- Ensure a second work-start event for the same workflow work item cannot appear for a different runner without an intervening durable ownership-transition event.
- Recover project backlog membership from authoritative durable workflow data, using workflow variables when metadata annotations are missing.
- Keep active agent session read models, workflow leases, and agent activity/status APIs consistent about the active runner owner.
- Add tests for activation recovery, duplicate-dispatch prevention, project-scoped backlog recovery, and ownership consistency.

**Non-Goals:**

- Do not add multi-workflow runner concurrency; that remains part of issue #22.
- Do not fully solve stale terminal backlog cleanup; that remains part of issue #25.
- Do not change active task timeline styling; that remains part of issue #21.
- Do not redesign liveness timeout accuracy; that remains part of issue #26.
- Do not change public API request or response shapes unless required to surface an explicit recovery/blocked state already representable by existing models.

## Decisions

1. Treat persisted `WorkflowLease` as authoritative activation state.

   `WorkflowGrain.OnActivateAsync` will restore a loaded active lease into the grain's in-memory lease field instead of clearing it. The grain may still cache `_lastRunnerId` for diagnostics or continuity, but active ownership checks must use the restored lease. If the lease format lacks the current work item id or enough data to prove ownership, activation should classify the workflow as recovery-blocked rather than silently dispatchable.

   Alternatives considered: Clear the lease and rely on session state to detect duplicates. This was rejected because sessions are read models and can lag or disagree; the lease is the intended exclusive ownership guard. Another option was to release every lease during activation. That was rejected because activation is not evidence that the owner is dead, and it would create unnecessary duplicate attempts after benign grain reactivation.

2. Gate dispatch on lease reconciliation before work assignment.

   `GetWorkAsync` will check for an active lease before creating a new assignment, persisting a replacement lease, or emitting a start event. If the lease belongs to another runner and is valid, the workflow remains non-dispatchable for the requesting runner. If the same owner polls again, the engine should not create a new attempt or duplicate start event; it may return no new work or an idempotent representation only if the existing contract already supports that safely.

   Alternatives considered: Let the backlog skip leased workflows without involving the grain. This is useful as an optimization but insufficient because the grain is the final authority and must protect against stale backlog claims, races, and activation state. Another option was to overwrite leases on each poll. That was rejected because it is exactly the duplicate-dispatch failure mode.

3. Use existing runner recovery paths for stale leases.

   A persisted lease can become redispatchable only after the system determines that its owner is offline, timed out, unregistered, or otherwise explicitly abandoned. The implementation should reuse the same code path used for runner unregister and heartbeat timeout so lease release, workflow state update, agent session update, backlog requeue, and event emission remain consistent. If liveness cannot be determined, the workflow stays blocked/non-dispatchable and should expose a diagnostic recovery state instead of assigning a second owner.

   Alternatives considered: Add a new ad hoc activation cleanup path that deletes leases. This was rejected because it would bypass existing failure semantics and hide why ownership changed. Another option was to always trust leases forever. That prevents duplicates but can strand work indefinitely when a runner is truly gone; explicit recovery gives correctness with an operator-visible path back to progress.

4. Emit work-start events only after durable lease ownership exists.

   The dispatch sequence should be lease-first: determine assignability, persist the lease for the selected runner and work item, then emit `workflow_task_started` or equivalent start events using the same runner/work item values. Ownership transfer events such as abandonment, expiration, interruption, failure, retry, or handoff must be durable before a different runner can receive and start the same work item.

   Alternatives considered: Keep event emission before lease persistence for responsiveness. This was rejected because event streams then describe ownership that may not exist durably. Another option was to deduplicate events after the fact. That was rejected because consumers need an accurate causal timeline, not post-hoc cleanup.

5. Resolve project identity through a durable fallback chain during backlog recovery.

   `WorkflowBacklogRecoveryService` should resolve project id from indexed metadata when present, then metadata annotations, then persisted workflow variables or another durable workflow binding. Only if no authoritative source exists should recovery avoid claiming the workflow and record an explicit diagnostic. It should not silently register projectless workflows into `default`.

   Alternatives considered: Continue defaulting missing metadata to `default`. This was rejected because it creates cross-project backlog pollution and duplicate claims. Another option was to backfill metadata before recovery and depend on the backfill. That can be a migration aid, but recovery still needs a robust fallback for old or partially written rows.

6. Make lease ownership the source of truth for active owner reads.

   Agent activity/status reads and workflow agent session read models should reconcile against active lease ownership before reporting a running work item. If a session says runner A is active while the durable lease says runner B owns the same work item, the read path should not present both as valid. Prefer updating or filtering stale session state after a durable handoff; if reconciliation is unsafe, surface a recovery/inconsistent state rather than a misleading active owner.

   Alternatives considered: Let each read model report independently. This was rejected because it exposes contradictory ownership to users and masks correctness problems. Another option was to make sessions authoritative. That was rejected because sessions describe execution telemetry, while leases enforce ownership.

## Risks / Trade-offs

- [Risk] Restoring leases can temporarily block workflows whose owner is gone but not yet recognized as offline -> Mitigation: trigger or reuse runner unregister/heartbeat timeout recovery and expose blocked recovery diagnostics when liveness is unknown.
- [Risk] Tightening dispatch checks can reduce throughput if backlog queues contain many leased workflows -> Mitigation: skip leased workflows during backlog recovery/selection as an optimization, while keeping grain-level checks as the correctness boundary.
- [Risk] Existing databases may contain inconsistent lease/session/event state from prior duplicate dispatches -> Mitigation: reconcile future reads against leases and use explicit diagnostics for mismatches; avoid destructive automatic cleanup unless the existing recovery path can prove the correct transition.
- [Risk] Project identity fallback from workflow variables may encounter older rows with missing or malformed variables -> Mitigation: do not default silently; record a recovery diagnostic and leave the workflow unclaimed until project identity is repaired.
- [Risk] Reusing runner timeout/unregister paths from activation or recovery may introduce ordering issues with event/session updates -> Mitigation: make ownership transition durable before requeue/redispatch and cover the order with integration tests.

## Migration Plan

1. Update `WorkflowGrain.OnActivateAsync` to keep restored active leases and initialize any derived owner fields from that restored state.
2. Update `GetWorkAsync` and related dispatch helpers so active leases block new assignments and start events until explicit release/recovery.
3. Route stale lease handling through the existing runner unregister/heartbeat timeout abandonment path, including lease release, session update, workflow event emission, and backlog requeue.
4. Update backlog recovery to resolve project id from durable metadata/variables and to avoid creating backlog claims for workflows with valid active leases.
5. Update agent activity/status/session read model logic so reported active owner agrees with the active lease or surfaces a recovery inconsistency.
6. Add regression tests for grain reactivation with a persisted lease, duplicate polling by another runner, same-owner polling without duplicate start, stale lease recovery before redispatch, project recovery from workflow variables, and lease/session owner mismatch handling.
7. Deploy as a backend-only change with no public API shape change.

Rollback strategy: revert the backend changes if dispatch stalls or recovery diagnostics reveal unexpected data shapes. Before rollback, capture affected `WorkflowLeases`, `WorkflowAgentSessions`, `WorkflowEvents`, and backlog state so any duplicate ownership or blocked workflows can be manually reconciled. Rolling back restores old dispatch behavior, so it should be treated as an emergency option because it reopens the duplicate-dispatch risk.

## Open Questions

- What exact existing event name should represent activation-time stale lease recovery if the owner is already offline: abandonment, expiration, interruption, failure, retry, or a dedicated handoff/recovery event?
- Should same-owner `GetWorkAsync` polling return an idempotent representation of the existing assignment, or should it always return no new work to avoid any chance of duplicate agent starts?
- Where should recovery diagnostics for missing project identity or unreconciled lease/session mismatch be stored so operators and the UI can inspect them consistently?
- Are there legacy workflow variable keys beyond the current project/issue variables that recovery must support when resolving project identity?
