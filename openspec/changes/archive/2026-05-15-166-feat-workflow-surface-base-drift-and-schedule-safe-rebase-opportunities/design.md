## Context

Mohist already has the primitives needed to re-align an issue branch with the project base: `rebase-branch` executes as visible WorkflowRun work, `checkSquashMergeability` records merge-ready snapshots, Check approval carries review and merge-ready evidence, and Integrate performs a final stale-evidence preflight before delivery side effects. The missing layer is a product-level drift decision that connects base advancement to active issue candidates before users reach approval or integration.

The implementation should treat drift as issue-candidate state, not as a stage failure. The workflow runtime remains the authority for scheduling and invalidation; API, CLI, Web UI, and SSE consume projected drift/rebase state instead of each caller re-deriving policy.

## Goals / Non-Goals

**Goals:**

- Detect when active Plan, Build, Check, or pre-delivery Integrate candidates were last observed against an older base position.
- Produce one normalized rebase opportunity decision: `skip`, `suggest`, `enqueue`, `defer`, or `needs-attention`.
- Protect running mutating agent work by deferring automatic rebase until a safe window.
- Schedule automatic rebase only as a visible `rebase-branch` WorkflowRun task in the current stage.
- Invalidate Check review, merge-ready, and approval evidence when drift or rebase makes it stale.
- Expose drift, decision, defer reason, stale evidence, conflict files, and next action through API, CLI, Web UI, and SSE refresh paths.

**Non-Goals:**

- No replacement for the existing manual rebase API or `rebase-branch` task handler.
- No new final merge strategy and no change to squash merge semantics.
- No requirement to use a specific dry-run merge algorithm such as `git merge-tree`.
- No progressive rebase or conflict-count risk threshold.
- No detailed database schema design in this artifact; persistence can use the existing WorkflowRun/projection patterns plus later schema decisions.
- No delegation of rebase scheduling to `merge-ready`; merge-ready remains a read-only mergeability check.

## Decisions

### D1: Add a dedicated drift evaluator above git and below workflow callers

Introduce a small `BaseDriftService` style boundary that accepts an issue, project, active WorkflowRun/stage-state facts, and current git facts, then returns a normalized `BaseDriftState`:

```ts
type RebaseDecision = 'skip' | 'suggest' | 'enqueue' | 'defer' | 'needs-attention';

interface BaseDriftState {
  drifted: boolean;
  baseBranch: string;
  observedBaseSha: string | null;
  currentBaseSha: string | null;
  candidateHeadSha: string | null;
  mergeBaseSha: string | null;
  decision: RebaseDecision;
  safeWindow: boolean;
  deferReason?: 'agent-running' | 'task-running' | 'waiting-for-task-boundary' | 'rebase-already-pending';
  staleEvidence?: {
    review: boolean;
    mergeReady: boolean;
    approval: boolean;
  };
  conflicts?: string[];
  message: string;
}
```

The service should compute git facts through existing `WorktreeManager` operations where possible: current base ref, candidate head, merge base, worktree status, and existing merge-ready snapshot output. The observed base position should be taken from the most authoritative current evidence in this order: last completed `rebase-branch` output, latest passing `merge-ready` snapshot, Check approval `mergeReadySnapshot`, Integrate delivery facts, then the candidate merge base at first observation if no stronger evidence exists.

**Alternatives considered:** Putting drift checks directly inside `merge-ready` was rejected because it would mix scheduling policy into a read-only check. Putting drift logic separately in CLI/Web UI was rejected because it would duplicate policy and create inconsistent decisions.

### D2: Base advancement triggers scan-and-project, not immediate branch mutation

When Integrate completes `integrate:merge` and advances the base branch, Mohist should emit a `base_branch_advanced` event and synchronously or shortly-after scan active issues in Plan, Build, Check, and Integrate before merge delivery. Each candidate receives an updated drift projection and a rebase opportunity decision. This pass may enqueue work only if the candidate is already in a safe window.

The scan should be idempotent. Re-running it for the same base SHA should update projections without duplicating `rebase-branch` tasks or repeating noisy attention events.

**Alternatives considered:** Rebasing every active issue immediately after base advancement was rejected because it can interrupt running coder sessions. Waiting until Integrate to notice drift was rejected because it preserves the current late-failure behavior.

### D3: Safe-window policy is derived from WorkflowRun state

Safe-window detection should use WorkflowRun as the current runtime authority:

- Safe: current StageRun is `awaiting-approval`.
- Safe: current StageRun is `running` with no running task and `nextWork()` is a task boundary or check boundary.
- Safe: stage is idle between tasks/checks, including after a Build task completes before the next task starts.
- Unsafe: any mutating task is running, especially Build coder tasks, repair tasks, health-fix tasks, rebase, conflict resolution, or Integrate delivery tasks.
- Unsafe: Integrate has passed its delivery freeze point or is running delivery side effects.

If unsafe, the decision is `defer` and the projection records a user-readable reason such as `agent-running` or `waiting-for-task-boundary`. The workflow engine should call the drift evaluator again after task completion, approval request, retry/rerun, and resume-decision points so deferred opportunities become actionable without a background branch mutation.

**Alternatives considered:** Detecting safety from agent session tables alone was rejected because session liveness is not the same as workflow mutability. Using only issue stage/status was rejected because it cannot distinguish an idle Build stage from a running Build task.

### D4: WorkflowRun schedules drift-driven rebase through the existing task path

For `enqueue`, call the existing `WorkflowApplicationService.scheduleRebaseTask` path with a drift-specific reason and caused-by metadata. `WorkflowRun.scheduleRebaseTask` should remain the single method that appends `rebase-branch`, reopens awaiting approval when necessary, and deduplicates pending/running rebase tasks.

The rebase task output already reports base branch, before/after base SHA, before/after head SHA, `shaChanged`, and conflicts. Extend projection and UI handling around this output rather than adding a second hidden rebase executor.

**Alternatives considered:** Calling `WorktreeManager.rebaseOntoMaster` directly from the drift service was rejected because it would bypass task ordering, auditability, and stage-state UI. Creating a separate queue-only rebase worker was rejected for the same reason.

### D5: Evidence invalidation is explicit and happens before approval is actionable

When drift is detected in Check and the latest review, merge-ready, or approval evidence references an older `baseSha`, `mergeBaseSha`, or candidate head, the Check stage must be projected as having stale evidence. If an approval is currently awaiting, it should no longer be user-actionable: clear or mark the approval projection stale, emit evidence invalidation events, and show guidance to rebase/rerun Check.

After `rebase-branch` completes, existing `shaChanged` logic should continue to invalidate Check task/check state. This change extends invalidation to base drift even before a rebase happens: a changed base can make merge-ready and approval evidence stale even if candidate head has not changed.

Approval command handling should also verify freshness at submit time. If a stale approval slips through due to a race, `approveStage` or its application-service wrapper should reject it with a clear stale-evidence error and leave the stage requiring rebase/rerun instead of advancing to Integrate.

**Alternatives considered:** Relying only on Integrate's stale snapshot preflight was rejected because the user would still see and approve stale Check evidence. Invalidating only after successful rebase was rejected because drift alone can invalidate merge-ready evidence.

### D6: Read APIs expose drift as a projection, not as UI-only decoration

Add drift state to issue list/show and stage-state responses so all clients render the same facts. The response shape should include `drifted`, `decision`, `safeWindow`, `deferReason`, `staleEvidence`, `observedBaseSha`, `currentBaseSha`, optional `conflicts`, and `nextAction` text.

CLI should render a compact section in `mo issue show <number>` and optionally status markers in list views. Web UI should show issue-card badges, Issue Detail guidance, stale-approval replacement copy, rebase task progress through the canonical task list, and conflict diagnostics from `rebase-branch` output or `rebase_conflict` events.

**Alternatives considered:** Emitting only SSE events was rejected because reconnecting clients and CLI commands need durable/readable state. Adding bespoke rebase UI state separate from stage-state was rejected because Issue Detail already uses WorkflowRun-backed task state as the canonical progress surface.

### D7: Events are notifications for refresh and audit, not the source of truth

Add typed events for the domain moments that clients need to refresh or explain: base branch advanced, candidate observed base, base drift detected, rebase opportunity opened, active work protected, safe rebase window opened, rebase decision made, rebase task scheduled, candidate rebased, candidate evidence invalidated, and user attention requested.

Events should include issue id, project id, issue number, base branch, observed/current base SHA when available, decision, and reason. The state projection remains authoritative; event handlers should invalidate React Query/API caches and show toasts only when useful.

**Alternatives considered:** Encoding every transition only in workflow logs was rejected because live UI needs typed refresh signals. Making SSE the primary state was rejected because it is transient and not available to CLI.

### D8: Regression tests exercise policy, not incidental UI timing

Backend tests should cover two critical flows:

- Base advances while a Check issue has passing review, passing merge-ready, and awaiting approval. Drift detection marks evidence stale, approval is no longer actionable, and approval submission is rejected until rebase/rerun refreshes evidence.
- Base advances while a Build task is running. Drift detection records `defer` with a protected-work reason, no `rebase-branch` task is appended, and the task is appended or suggested only after task completion creates a safe boundary.

Frontend/CLI tests should verify that projected drift state renders as user guidance and that stale approval actions are hidden or replaced.

**Alternatives considered:** Testing only final Integrate preflight was rejected because the feature is explicitly about earlier visibility and scheduling.

## Risks / Trade-offs

- [Risk] Observed base position may be missing for older active issues. → Mitigation: compute an initial observation from current merge base and mark confidence in the projection; do not fail the workflow solely because historical evidence is absent.
- [Risk] Drift scans can become expensive if they run git commands for many active issues after every merge. → Mitigation: limit scans to active issues in the current project, cache the current base SHA per scan, skip closed/done candidates, and avoid repeated scans for the same base SHA when the projection is already current.
- [Risk] Race between approval submission and base advancement could approve stale evidence. → Mitigation: perform freshness validation inside the approval command path in addition to projection-time UI suppression.
- [Risk] Over-eager automatic rebase could surprise users in Plan or Check approval. → Mitigation: use `suggest` for user-decision states unless policy explicitly chooses `enqueue`, and always schedule as visible WorkflowRun work with caused-by metadata.
- [Risk] Evidence invalidation can cause repeated Check reruns if base advances frequently. → Mitigation: keep drift as non-failure state, deduplicate rebase tasks, and show clear defer/suggest state instead of repeatedly failing stages.
- [Risk] Conflict diagnostics may be split between rebase task output and existing `rebase_conflict` SSE. → Mitigation: project conflict files and failure reason from the terminal task result into stage-state so durable UI and CLI do not depend on live SSE history.

## Migration Plan

1. Add the drift evaluator and projection types behind API fields that default to `null` or `skip` when no active candidate/worktree exists.
2. Wire base advancement detection after successful Integrate merge and add idempotent active-issue scanning.
3. Add safe-window re-evaluation hooks after task completion, approval request, stage resume, retry, and rerun paths.
4. Extend WorkflowRun scheduling/invalidation paths to carry drift caused-by metadata and stale evidence outcomes.
5. Extend issue list/show and stage-state API responses, then CLI and Web UI rendering.
6. Add backend regression tests for Check stale evidence and Build protected work, followed by focused CLI/Web UI rendering tests.
7. Rollback strategy: disable drift scan/event wiring while leaving manual rebase, merge-ready, approval, and Integrate preflight behavior unchanged.

## Open Questions

- Should the default safe-window decision for Plan/Check awaiting approval be `suggest` or `enqueue`? The product text allows either; the implementation should choose one explicit policy before specs/tasks are finalized.
- Should Integrate before delivery ever auto-enqueue rebase, or should it always become `needs-attention` because delivery side effects are imminent?
- What exact persistence location should own observed base position and drift projection once schema design is allowed: WorkflowRun snapshot metadata, stage-state projection, issue metadata, or a dedicated projection table?
