## Context

The current pipeline has enough post-merge verification to avoid marking every Check approval as Done, but the integration path is still hidden across several places. `CheckStageRunner` returns `Stage.Done` and still calls `archiveChange()` as a side effect; the approval endpoint special-cases Check approval by enqueueing `MergeQueue`; `MergeQueue` may rebase, delegate conflict resolution, run build verification, delegate build fixes, merge, and then call `PostMergeFinalizer`; `PostMergeFinalizer` records `health:postMerge` against a Check execution and directly sets the issue to Done.

This creates two design problems. First, the user cannot observe a clear stage boundary between accepting a candidate and integrating it. Second, OpenSpec canonical specs are not updated before change archival, so `openspec/specs/` is not a reliable source of truth after issues complete.

The implementation should preserve the existing stage runner architecture, health gate implementation, stage execution records, worktree manager, and archive directory mechanics where they are useful. The main change is to pull integration responsibilities into a visible, deterministic stage and keep AI/code-fix behavior out of that stage.

## Goals / Non-Goals

**Goals:**

- Add `Stage.Integrate` to the backend and frontend stage model and make the normal execution path `Backlog/Draft -> Plan -> Build -> Check -> Integrate -> Done`.
- Make Check approval transition to Integrate, not directly to a hidden merge/finalization path.
- Add an `IntegrateStageRunner` that performs OpenSpec spec sync, OpenSpec change archive, merge/rebase landing, final integration health verification, and Done transition in order.
- Add an `OpenSpecIntegrator` service with deterministic dry-run and apply operations for delta specs.
- Move final health verification semantics from hidden `postMerge` finalization into Integrate while keeping existing `healthGates.postMerge` configuration compatible.
- Ensure Integrate failures are visible, typed by failing step, recoverable, and never trigger agent conflict resolution or build-fix agents.
- Expose integration readiness and integration evidence through existing issue detail, stage execution, API, event, and UI surfaces.

**Non-Goals:**

- Do not introduce AI-assisted spec merging or requirement rewriting.
- Do not rebuild historical archived changes into `openspec/specs/`.
- Do not introduce Release or Deploy stages.
- Do not make Integrate an approval gate; approval remains in Check.
- Do not refactor the entire workflow engine or replace the existing stage runner model.
- Do not change issue archive or worktree cleanup semantics except where they must avoid being confused with OpenSpec change archive.

## Decisions

### D1: Add Integrate As A Normal Stage Runner

Add `Stage.Integrate = 'integrate'`, insert it into `STAGE_ORDER`, update `STAGE_TRANSITIONS`, and register `IntegrateStageRunner` alongside Plan, Build, and Check. `CheckStageRunner.getNextStage()` should return `Stage.Integrate`; it must stop calling `archiveChange()`.

`WorkflowEngine` should continue looping until `Stage.Done`, but the special guard that rejects `Check -> Done` can be simplified to the normal transition model. The engine should allow `IntegrateStageRunner` to return `Stage.Done` only after all integration steps pass. Completion cleanup (`clearApprovalState`, `IssueStatus.Completed`, checkpoint deletion) remains in the engine so Done finalization has one owner.

**Alternatives considered:** Keep Check as the last runner and treat Integrate as merge state only. This was rejected because the user needs a first-class observable pipeline stage, not another hidden state under Check or Done.

### D2: Check Approval Resumes Pipeline Instead Of Enqueueing MergeQueue

The approval endpoint should handle Check approval like Plan approval: mark `approvalState.status = 'approved'`, transition the issue to `Stage.Integrate`, clear or preserve approval state only as needed for evidence, and enqueue `resume-pipeline`. It should not call `mergeQueue.enqueue()` directly.

The existing snapshot validation before approving Check should remain, but its output should be extended to include integration readiness from Check checks. If code changed after the Check suite snapshot, the endpoint should continue re-running Check before allowing Integrate.

**Alternatives considered:** Keep Check approval enqueueing MergeQueue and have MergeQueue update the stage to Integrate. This was rejected because MergeQueue is a merge executor, not the workflow coordinator; letting it own stage transition would keep orchestration split across unrelated modules.

### D3: Create A Dedicated OpenSpecIntegrator Service

Add `packages/cli/src/openspec/open-spec-integrator.ts` or equivalent. Its public interface should be small and explicit:

```ts
type SpecSyncMode = 'dry-run' | 'apply';

interface OpenSpecIntegrator {
  preview(changeDir: string, projectPath: string): Promise<SpecSyncSummary>;
  apply(changeDir: string, projectPath: string): Promise<SpecSyncSummary>;
}
```

The service owns parsing delta spec files, reading main specs, validating conflicts, applying deterministic mutations in memory or on disk, and returning a structured summary. `ChangeArtifactsManager.archiveChange()` remains a low-level directory move and is only called by Integrate after `apply()` succeeds.

**Alternatives considered:** Put spec sync into `CheckStageRunner` or `ChangeArtifactsManager`. This was rejected because Check must be read-only for main specs, and `ChangeArtifactsManager` should not understand OpenSpec requirement semantics.

### D4: Implement Deterministic Requirement-Block Delta Apply

The first implementation should parse markdown by requirement block instead of building a full Markdown AST. A requirement block starts at headings matching `### Requirement: <name>` and runs until the next same-or-higher-level heading. Delta sections are recognized by `## ADDED Requirements`, `## MODIFIED Requirements`, `## REMOVED Requirements`, and `## RENAMED Requirements`.

Apply order is fixed: renamed, removed, modified, added. Validation runs before writing any files:

- `MODIFIED`, `REMOVED`, and `RENAMED FROM` must match an existing requirement in the target main spec.
- `ADDED` and `RENAMED TO` must not duplicate an existing target requirement.
- Requirement names must be unique inside each source delta and resulting target spec.
- Each added, modified, and renamed-to requirement block must contain at least one scenario heading.
- Unknown or malformed delta sections fail the sync instead of being silently ignored.

For missing target capability specs, only added requirements may create a new `openspec/specs/<capability>/spec.md`. Modified, removed, or renamed deltas against a missing target spec fail.

**Alternatives considered:** Use an LLM to merge deltas into natural-language specs or defer sync until after archive. Both were rejected because Integrate must be deterministic and must not archive a change before canonical specs are updated.

### D5: Add Check Readiness Checks Without Writing Integration Artifacts

Add Check-stage checks for OpenSpec dry-run and mergeability/rebase feasibility. These should run after build/test health and before user approval. They produce structured output stored in stage execution check results and approval output:

- `openspec-sync-dry-run`: capabilities touched, added/modified/removed/renamed counts, target files, conflicts, and whether apply would succeed.
- `mergeability`: target branch, base SHA, candidate head SHA, whether fast-forward is possible, whether a clean rebase is possible, and conflict files if known.
- `health:integrate` preview: final gate policy name/command/timeout, using existing `postMerge` config for compatibility.

The dry-run check must not write `openspec/specs/` or archive the change. The mergeability check may run safe git commands and a rebase feasibility check only if it aborts/cleans up and does not commit or auto-resolve.

**Alternatives considered:** Wait until Integrate to discover spec or merge conflicts. This was rejected because the Check approval UI should show whether the candidate can be safely integrated before the user accepts it.

### D6: Replace PostMergeFinalizer With Integrate Final Verification Semantics

Retain the health-gate command/config compatibility but move ownership into Integrate. The code can either rename `PostMergeFinalizer` to `IntegrationHealthGate` or wrap it behind a new service, but it must stop directly setting Done and must record results under `Stage.Integrate` with UI-facing name `health:integrate`.

`healthGates.postMerge` should remain accepted in `workflow.yaml` to avoid breaking existing configuration. Internally, `loadHealthGatePolicies()` may expose both `postMerge` and `integrate`, with `integrate` defaulting to `postMerge`, or the runner may explicitly read `postMerge` and label it as Integrate in outputs.

**Alternatives considered:** Rename config to `healthGates.integrate` immediately and remove `postMerge`. This was rejected because existing projects may already rely on `postMerge`; the semantic UI change does not require a breaking config change.

### D7: Make Integrate Merge No-Code-Fix And Best-Effort Rebase Only

Integrate should not use the current `MergeQueue.processItem()` path because that path can call `resolveConflicts()` and `fixBuildErrors()`. Instead, add a small merge method used by `IntegrateStageRunner`, either in `WorktreeManager` or a new `IntegrationMerger`:

```ts
mergeApprovedCandidate(opts): Promise<MergeTruth | IntegrationFailure>
```

The method may fast-forward merge when possible. If rebase is required, it may attempt a normal clean rebase with `abortOnConflict: true` and then fast-forward merge. It must not use `--theirs`, agent conflict resolution, automatic build fixes, or commits that change product code. If conflicts or dirty state prevent a clean landing, it returns `failingStep: 'merge'` with actionable details.

`MergeQueue` can remain for older direct merge/retry features during migration, but Check approval and normal pipeline integration must bypass it. Any direct merge API should call the same Integrate service or be disabled unless it can preserve the Integrate contract.

**Alternatives considered:** Add a `noAutoFix` flag to `MergeQueue`. This is possible but less clear because `MergeQueue` already mixes queueing, rebase, agent conflict resolution, build fix, merge, and finalization. A focused integration merge path is a deeper module with a simpler contract.

### D8: Store Integration Evidence In StageExecution First

Use `stage_executions` as the primary evidence surface for Integrate. The Integrate runner creates one execution for `Stage.Integrate`; each integration step appends a check-like result or task result with structured `output`:

- `integrate:spec-sync`
- `integrate:archive-change`
- `integrate:merge`
- `health:integrate`
- `integrate:complete`

The output should include summaries, archive path, merge truth, command metadata, log excerpt, and failure details. If a compact latest-state field is needed for issue cards, add a nullable JSON column such as `issues.integration_state` only after verifying that stage execution data is insufficient for list views. Avoid creating a broad integration table in the first implementation.

**Alternatives considered:** Add a dedicated integration table immediately. This was rejected for the first version because stage executions already model stage-scoped evidence and are exposed by `GET /api/issues/:number/executions`.

### D9: Use Typed Integration Events For Live UI

Add EventBus/SSE events for integration progress instead of overloading merge events:

```ts
integration_started
integration_step_updated
integration_completed
integration_failed
```

Each step update includes `step`, `status`, `summary`, and optional `output`. Existing `merge_started`, `merge_completed`, and `merge_failed` may still be emitted by lower-level merge code for compatibility, but the UI should use integration events and stage execution history for the Integrate panel.

**Alternatives considered:** Reuse only `stage_task_update` and merge events. This was rejected because Integrate has domain-specific evidence and failure categories that would be lossy if forced into generic task labels.

### D10: Frontend Treats Integrate As A Distinct Non-Done State

Add `Stage.Integrate` to frontend types, stage order, kanban/pipeline columns where applicable, `PipelineStatusTimeline`, approval/readiness panels, SSE subscriptions, and issue-card badges. Issues in Integrate should show “Integrating” or a failed integration reason, never Done. Done cards should only show archive actions when status is Completed and integration evidence exists or merge state is Merged.

The Check approval panel should read readiness/evidence from the latest Check stage execution. The Done detail view should read the latest Integrate execution and display spec sync summary, archive path, merge truth, and final health result.

**Alternatives considered:** Hide Integrate in UI and only show merge states. This was rejected because observability is one of the main reasons for the change.

## Risks / Trade-offs

- [Risk] Markdown requirement parsing may miss unusual spec formatting. → Mitigation: support only the documented OpenSpec heading format, fail closed on malformed sections, and add parser fixture tests for added, modified, removed, renamed, duplicate headers, missing scenarios, and missing target specs.
- [Risk] Spec sync succeeds and archive succeeds, but merge later fails, leaving main specs updated on the candidate branch but not landed. → Mitigation: perform spec sync and archive inside the issue worktree before merge; if merge fails, the canonical target branch remains unchanged, and the issue stays in Integrate for user repair. The user can return to Build/Check with the worktree as the source of truth.
- [Risk] A clean rebase during Integrate rewrites candidate commits and changes metadata even though code is unchanged. → Mitigation: allow only clean git rebase without conflict resolution or generated commits; record base/head/merge truth so users can see exactly what landed.
- [Risk] Existing direct merge and retry APIs bypass Integrate. → Mitigation: route direct merge through the same integration service or return a clear error instructing users to approve Check/resume Integrate; update tests that assert direct merge cannot bypass final verification.
- [Risk] Existing `healthGates.postMerge` users are confused by `health:integrate` naming. → Mitigation: document that `postMerge` remains a compatibility config key while UI and results use Integrate final verification terminology.
- [Risk] Stage execution results become overloaded with complex integration evidence. → Mitigation: keep output schemas compact and step-scoped; add a separate issue-level summary only if list/detail API performance or UX requires it.
- [Risk] Server restart during Integrate can repeat non-idempotent steps. → Mitigation: make each step idempotent where possible: detect already-applied spec sync by comparing resulting blocks, detect already-archived change by issue-number archive lookup, detect already-merged branch by merge-base, and resume from recorded successful step evidence.
- [Risk] Integrate failures block issues and require user action, increasing friction. → Mitigation: expose precise failing step, file/capability/conflict details, and next action; allow retry after the user returns to Build/Check and re-approves.

## Migration Plan

1. Add backend and frontend `Stage.Integrate` enums, stage order, transition rules, labels, filters, and pipeline timeline rendering.
2. Add `OpenSpecIntegrator.preview/apply` with deterministic parser/apply tests before wiring it into the workflow.
3. Add Check dry-run checks for spec sync and mergeability, store their outputs in stage execution check results, and include readiness in approval output.
4. Change `CheckStageRunner` to return `Stage.Integrate` and remove its archive side effect.
5. Add `IntegrateStageRunner` with step recording and event emission; first wire spec sync/archive, then merge, then final health gate, then Done transition.
6. Move or wrap `PostMergeFinalizer` so Integrate owns final health verification and `health:postMerge` config maps to `health:integrate` output.
7. Change Check approval handling to set approval approved, transition to Integrate, and enqueue `resume-pipeline`; remove normal-path use of `MergeQueue.enqueue()` from approval.
8. Update direct merge/retry/rebase endpoints and recovery logic so they cannot bypass Integrate and so recoverable Integrate issues resume through `IntegrateStageRunner`.
9. Update frontend issue cards, issue detail, approval panel, pipeline timeline, SSE event handling, and evidence rendering.
10. Add regression tests for Check approval entering Integrate, dry-run conflict blocking approval, Integrate success path, each Integrate failure step, no agent conflict/build-fix in Integrate, final health gate failure blocking Done, and UI stage rendering.

Rollback strategy: because this changes persisted stage values, rollback should include a small data repair script or migration note mapping active `integrate` issues back to `check` with a blocked reason such as “Integrate rollback required.” Completed Done issues do not need rollback. The implementation should avoid destructive DB schema changes; any added nullable evidence fields should be ignored safely by older code.

## Open Questions

- Should direct `POST /api/issues/:number/merge` remain as a supported escape hatch by invoking Integrate, or should it be deprecated in favor of Check approval only?
- Should integration summaries be stored only in `stage_executions`, or should `issues` gain a compact `integration_state` JSON field for faster list/card rendering?
- What exact rename syntax should delta specs use for `RENAMED Requirements` so the parser can validate `from` and `to` without ambiguity?
- Should a failed Integrate automatically transition the issue back to Build, or stay blocked/interrupted at Integrate until the user explicitly chooses the next action?
