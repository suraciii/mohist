## Context

`IntegrateStageRunner` is the only stage runner that still bypasses the shared `BaseStageRunner` contract. It executes `integrate:spec-sync`, `integrate:archive-change`, `integrate:merge`, and final health verification as runner-local steps, manually emits `integration_*` events, and appends task evidence directly to stage execution history.

That implementation preserves the side effects and ordering of Integrate, but it does not seed or update the `workflow_run` task/check model that the Web UI now prefers. `WorkflowRunService` currently seeds Plan tasks/checks only, `StageStateService` has no static Integrate tasks, and `TaskProgressPanel`/`PipelineView` render Integrate progress primarily from `workflowRun.stageRuns`. As a result, Integrate appears as a running stage without visible step-by-step progress.

This design should standardize Integrate on the existing task/check architecture with the smallest viable change set. The side effects, ordering, workflow configuration key (`healthGates.postMerge`), and compatibility `integration_*` events should remain intact unless they directly block the migration.

## Goals / Non-Goals

**Goals:**
- Make Integrate use the normal `BaseStageRunner` lifecycle: execute tasks, then run checks, then let shared failure handling decide retries or stage failure.
- Persist the three ordered Integrate work steps as standard tasks in `workflow_tasks` and the final health gate as a standard check in `workflow_checks`.
- Reuse existing task/check mirroring so WorkflowRun-backed UI surfaces show live Integrate progress and durations.
- Reuse existing health gate infrastructure and config parsing for final verification instead of keeping a separate Integrate-only runner implementation.
- Preserve existing Integrate behavior for spec sync, archive, merge, and final verification ordering.

**Non-Goals:**
- Redesign Integrate semantics, stage order, or merge strategy.
- Remove compatibility `integration_started` / `integration_step_updated` / `integration_failed` / `integration_completed` events in this change.
- Introduce a new workflow config schema for Integrate health gates; `healthGates.postMerge` remains the source of truth.
- Rework the general stage-state or WorkflowRun data model beyond what is required to seed and render Integrate tasks/checks.

## Decisions

### D1: Model Integrate as three ordered tasks plus one post-task check

Integrate will expose `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge` as standard stage tasks returned and recorded via `appendTaskResult()`. Final verification will move out of `executeTasks()` and become a single post-task check, `health:integrate`, produced by `getChecks()`.

This keeps the existing ordering contract intact while aligning Integrate with the architecture already used by Plan, Build, and Check: deterministic work happens in tasks, then verifications happen in checks. It also fixes the current mismatch where `final-health` is shown like a step but semantically behaves like a gate.

**Alternatives considered:**
- Keep all four current steps as tasks, including final health: rejected because it would continue to bypass shared check classification and fix/recheck flow.
- Split each side effect into its own reusable task object abstraction: rejected as unnecessary churn for a runner-local refactor.

### D2: Seed Integrate tasks and checks at WorkflowRun creation time

`WorkflowRunService.startRun()` will seed the Integrate `StageRun` with the static tasks `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge`, plus a static check `health:integrate`, similar to how Plan seeds its initial artifacts and checks.

The runner will still update those rows dynamically through existing `workflowRunService.upsertTask()` and `upsertCheck()` calls inside `BaseStageRunner`, but pre-seeding is important so Integrate has a complete visible contract before execution starts and while it is still pending.

`StageStateService` should also define the same static Integrate task ids so the fallback read model stays aligned with WorkflowRun and so tests that still exercise stage-state-only paths do not drift.

**Alternatives considered:**
- Create Integrate task/check rows lazily only when execution begins: rejected because the UI would still have an empty stage until the first side effect runs.
- Seed only WorkflowRun and leave `StageStateService` empty: rejected because the fallback read path and test fixtures would remain inconsistent.

### D3: Reuse `HealthGateCheck` and extend `runHealthFixTask` for Integrate

The existing `HealthGateCheck` already encapsulates the command execution, timeout handling, concise failure message, and output shape expected for health checks. Integrate should instantiate `new HealthGateCheck({ worktreePath, policy, stage: 'integrate' })` from `getChecks()` and continue loading policy from `loadHealthGatePolicies(workflow).postMerge`.

For recovery, Integrate should use the existing health-fix flow instead of inventing a special-case fixer. That means extending `runHealthFixTask` option types to allow `taskId: 'fix-integrate-health'` and `stage: 'integrate'`, then adding a `CheckFailurePolicy` entry mapping `health:integrate -> fix-integrate-health` when `postMerge.autoFix` is enabled.

This keeps the health gate implementation single-sourced and preserves current config compatibility while making Integrate participate in standard fix-task and recheck semantics.

**Alternatives considered:**
- Keep `runFinalHealthGate()` inside `IntegrateStageRunner` and merely mirror its result into `workflow_checks`: rejected because it would still bypass `BaseStageRunner.handleCheckFailure()`.
- Create a new Integrate-only health check/fix implementation: rejected because it would duplicate logic already shared by plan/build/check health gates.

### D4: Keep runner-local task execution, but normalize event and result emission

`IntegrateStageRunner.executeTasks()` should stay as one sequential method that performs spec sync, archive, and merge in order, because these steps are tightly coupled and already encoded with the correct side effects and edge-case handling. The refactor should extract small private helpers per step if that reduces duplication, but should not introduce a new general task engine.

Each task step will emit the generic `stage_task_update` lifecycle used elsewhere and append a normalized `StageTaskResult` only once per finished attempt. Existing `integration_*` events should still be emitted as compatibility evidence, but they become secondary notifications rather than the primary UI contract.

This is the smallest change that moves user-facing state to the shared model without risking regressions in archive/merge/spec-sync behavior.

**Alternatives considered:**
- Rewrite Integrate around a new declarative task executor: rejected as too large for the problem.
- Remove `integration_*` events immediately: rejected because there are existing tests and API event types that still reference them.

### D5: Define Integrate failure policy only for post-task health verification in this change

The shared `CheckFailurePolicy` mechanism only applies to checks, not directly to task failures. In this refactor, Integrate will use it for `health:integrate` only. Failures in `integrate:spec-sync`, `integrate:archive-change`, or `integrate:merge` will continue to fail the task phase immediately and stop the stage, preserving current semantics.

If merge conflict auto-repair is later required as part of the standard framework, it should be introduced either as an explicit Integrate check or as a broader BaseStageRunner enhancement for task-failure repair. That is outside the scope of this design.

**Alternatives considered:**
- Force `integrate:merge` into the check system only to gain fix-task support: rejected because merge is a side effectful state transition, not a pure verification gate.
- Extend BaseStageRunner now to support task-failure policies as part of this issue: rejected because it broadens the change substantially and is not required to solve the visibility hole.

## Risks / Trade-offs

- [Integrate health check name diverges from current evidence] → Use `health:integrate` consistently in persistence while preserving compatibility event payloads and the existing `postMerge` config source.
- [Duplicated task definitions drift between WorkflowRunService and StageStateService] → Define Integrate task/check ids in shared constants or keep a single local definition reused by both seed paths.
- [UI still shows no progress if only compatibility events fire] → Make WorkflowRun/StageState rows the primary source of truth and treat events only as live-duration hints.
- [Refactor accidentally changes side-effect ordering] → Keep task execution strictly sequential and add regression tests that assert archive and merge do not run after failed spec sync, and final health does not run before successful merge.
- [Integrate auto-fix runs when projects expect manual intervention] → Only register `health:integrate` fix policy when `postMerge.autoFix` is enabled; otherwise let the stage fail locally with visible evidence.
- [Legacy tests or consumers still depend on `integration_*` events] → Preserve those events in the runner for now and update tests to assert both compatibility events and WorkflowRun persistence.

## Migration Plan

1. Add shared Integrate task/check definitions and seed them in `WorkflowRunService.startRun()` and the stage-state fallback model.
2. Refactor `IntegrateStageRunner` so `executeTasks()` returns after spec sync/archive/merge and `getChecks()` returns the post-merge health gate check.
3. Remove runner-local final health execution and wire Integrate check failure policy plus `fix-integrate-health` support through `runHealthFixTask`.
4. Keep emitting compatibility `integration_*` events while switching UI expectations and tests to assert `workflow_tasks` / `workflow_checks` visibility.
5. Update backend and frontend tests for WorkflowRun seeding, Integrate task/check rendering, health-gate retries, and no-regression side-effect ordering.

Rollback strategy:
- If the refactor regresses integration behavior, revert `IntegrateStageRunner` and the new Integrate seeding constants together so the stage returns to the previous self-managed flow.
- Because this change reuses existing tables and adds no schema migration, rollback is code-only.

## Open Questions

- The issue statement mentions `fix-merge-conflict`, but the current shared failure-policy framework only reacts to checks, not task failures. For this change, should merge conflict repair remain out of scope and preserve current fail-fast behavior, or is there a separate planned follow-up to extend repair policies to task failures?
