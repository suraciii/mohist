# Issue 567 Review

## Verdict

FAIL. The prior transient Session-write durability finding is repaired, but a confirmed active work can still lose its only Session visibility delivery when its bound Session grain has no persisted state.

## Must-Fix Findings

### MF-1: Missing AgentSession state is treated as a successful delivery

**Violates:** `agent-work-interruption-visibility`, "The AgentSession, AgentTurn, and workflow-task read models and API DTOs SHALL expose the update-caused interruption lifecycle for every work named by a durable update operation," including the `interrupting` / `interrupted` scenario at fence creation. It also violates the issue requirement that affected work remains explicitly visible with an actionable recovery path.

The new durable delivery queue is removed even when the target Session does not exist:

- Workflow delivery calls `session.GetAsync()` in `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.Reports.cs:818-825`; a null result returns normally. `DeliverPendingSessionInterruptionAsync` then removes that entry and persists the shortened queue at `:784-789`.
- AgentJob delivery has the same behavior in `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.Recovery.cs:623-630`, followed by queue removal and persistence at `:602-609`.

There is no invariant preventing this case. Workflow execution binding validates that the Session ID is non-empty in `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Work.cs:721-728`, but it does not verify that the Session state exists. A stale/deleted Session ID, or a binding arriving before Session materialization, therefore reaches the new queue. The owner grain can durably commit `interrupting` / `interrupted`, observe `GetAsync() == null`, discard the pending delivery, and later acknowledge the update or receipt with no AgentSession or AgentTurn lifecycle projection and no retry path.

Treat a missing Session as an unacknowledged delivery: retain the durable queue and retry it, or move the work to an explicit actionable unresolved state without claiming visibility was delivered. Add deterministic coverage for both Workflow and AgentJob with a non-materialized or temporarily unavailable Session, proving that the delivery is retained and later materialized/replayed rather than silently dropped.

## Re-review Disposition Checks

- **Previous MF-1 (owner commit followed by transient Session persistence failure):** fixed on the normal new-state path. WorkflowRun and AgentJobState now persist per-transition Session delivery obligations before owner acknowledgement; activation/reminder paths retry them, and duplicate receipt paths repair the Session projection before repairing the operation ledger.
- **Prior Workflow and AgentJob terminal-result receipt replay findings:** remain fixed. Exact replays still repair the operation settlement before returning the stored acknowledgement, and the new Session repair runs before that ledger repair. The current Workflow replay class passes 3/3.
- **Prior handoff cancellation observation:** remains fixed in `packages/runner/src/runtime/host.recovery.ts`; the current commits do not regress the per-request abort behavior.
- **Prior CLI `receipt-acked` observation:** remains non-blocking. The CLI can report receipt acknowledgement as recovered while retaining the underlying recovery phase; the plan treats the acknowledgement boundary as sufficient for bounded update reporting.
- **Prior update-operation retention observation:** remains non-blocking. Historical update operations are still retained without a visible compaction policy, but that does not by itself make the interruption/recovery result incorrect.

## Dimension Checks

- **Acceptance coverage:** FAIL. Immediate fencing, bounded shutdown, durable receipt replay, replacement dispatch, reconnect deduplication, per-work reporting, and explicit no-receipt terminal handling remain covered on the reviewed paths, but Session/AgentTurn visibility is incomplete when the bound Session is not materialized.
- **Correctness:** FAIL. The repair handles a Session persistence exception, but a normal null Session read is interpreted as success and permanently drops the durable visibility obligation.
- **Regression check:** checked, no additional must-fix regression found in the current queue/replay changes. Existing owner-state, receipt-ledger, handoff, recovery-generation, and bounded-reporting behavior remains intact.
- **Consistency:** checked, no additional issue. The persisted queue, activation/reminder retry, and idempotent Session projection follow the existing owner-grain durability patterns; the missing-state result handling is the remaining boundary defect.
- **Tests:** checked. The rebuilt Server SpecTests project passes with 0 warnings/errors; `AgentJobGrainSpecs` passes 22/22 and `RecoveryReceiptOperationReplaySpecs` passes 3/3. The tests cover transient AgentJob Session persistence failure and Workflow operation-replay failure, but do not cover a missing Session or transient Session failure in the Workflow fixture, so they do not close MF-1.

## Observations

1. The Workflow fixture no longer wires the `AgentSessionStatePersistenceFailureProbe`, so the symmetric Workflow queue path has no direct Session-store failure-injection test even though the implementation is intended to cover it.
2. The CLI continues to map `receipt-acked` to a user-facing recovered result before replacement execution has necessarily settled; the underlying recovery state remains available and the plan treats receipt acknowledgement as the bounded update boundary.
3. `RunnerUpdateOperationGrain` still retains historical operations in one persisted collection without a visible compaction policy. This is a maintenance concern outside the issue's acceptance criteria.

<promise>FAIL</promise>
