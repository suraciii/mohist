## Context

The current workflow runtime already persists relational run data in `workflow_runs`, `workflow_stage_runs`, `workflow_tasks`, and `workflow_checks`, but the decision logic is still distributed across `WorkflowEngine`, `BaseStageRunner`, concrete stage runners, `WorkflowRunService`, issue fields, `stage_executions`, `stage_states`, and check suites. This creates a shallow runtime model: tables exist, but any caller can still bypass ordering, approval, repair, failure, and stage-advance rules by writing directly to service methods or projections.

The implementation should turn WorkflowRun into the deep module that hides workflow progression rules. Callers should not need to know whether a stage is ready to complete, whether a fix task is allowed, whether the next stage is `check` or `integrate`, or whether post-merge health can be repaired. They should execute work and report facts; the aggregate decides.

P0 must stay compatible with the existing SQLite schema and active runs. It should not introduce an event store, DSL workflow engine, full attempt history, or removal of `tasks.json` as a Plan artifact.

## Goals / Non-Goals

**Goals:**

- Make `WorkflowRun` the only business authority for workflow state transitions.
- Enforce stage order, single active stage, task order, task-before-check, approval, fix-task, stage completion, Integrate freeze, and no-rollback invariants in domain methods.
- Replace public lifecycle CRUD writes with aggregate commands saved through one repository transaction.
- Keep runners responsible for external side effects only: agent calls, shell checks, artifact writes, git operations, spec sync, archive, and merge.
- Keep projections and APIs display-oriented: issue stage/status, `stage_states`, `stage_executions`, check suites, logs, and checkpoints can mirror or support the aggregate but cannot decide current business state.
- Materialize Build tasks from `tasks.json` into `workflow_tasks` and use WorkflowRun task state as runtime truth.
- Record Integrate delivery facts, including archive/spec-sync/merge outputs, landed sha, and post-merge freeze state.

**Non-Goals:**

- Do not implement durable domain events or event sourcing.
- Do not add full `TaskAttempt`, `CheckRun`, or `WorkflowEvent` history unless a small schema addition is required to preserve P0 facts.
- Do not introduce YAML/DSL workflow definitions as the source of stage behavior.
- Do not remove `tasks.json` as a Plan artifact or Build input.
- Do not redesign UI layout beyond consuming aggregate-backed API data.

## Decisions

### D1: Add A Domain Aggregate Separate From Persistence Rows

Create domain objects under a workflow domain boundary, for example `packages/cli/src/workflow/domain/`, with `WorkflowRun`, `StageRun`, `TaskRun`, `CheckState`, `StageDefinition`, and small command/result value types. These objects are constructed from repository snapshots and expose intention-level methods such as `startWorkflow`, `completeTask`, `recordCheckResult`, `approveStage`, `rejectStage`, `materializeTasks`, and `advanceAfterStageCompletion`.

The aggregate should return a `WorkflowDecision` containing updated aggregate state, domain events for in-process projection, and the next work request if any. Events are not persisted in P0; they are a structured way to apply projections and emit SSE/log events after saving.

**Alternatives considered:** Keep adding validation into `WorkflowRunService` methods. This leaves a shallow service with many bypassable CRUD-shaped entry points. Put all logic into `WorkflowEngine`. This keeps state rules coupled to orchestration and still leaks business decisions into runner loops.

### D2: Use An Application Service As The Only Workflow Runtime Facade

Introduce a `WorkflowApplicationService` that coordinates use cases:

```typescript
const run = repo.loadActive(issue.id)
const work = run.nextWork()
const execution = await runner.execute(work)
const decision = run.completeTask(work.stage, work.taskId, execution.result)
repo.save(run)
projections.apply(decision)
```

The service owns the transaction boundary: load aggregate, invoke one or more domain methods, save all affected run/stage/task/check rows, then update projections and emit notifications. Public callers should depend on this facade, not on repository row writers.

**Alternatives considered:** Let runners call aggregate methods directly. That still spreads transaction and projection knowledge into every runner. Let the repository call runner code while saving. That would mix external side effects into persistence and make retries unsafe.

### D3: Convert `WorkflowRunService` Into A Compatibility Facade Then Remove State-Mutating Shortcuts

Refactor `WorkflowRunService` in two steps. First, make existing call sites use new aggregate commands while keeping method names only as temporary adapters. Second, remove or make private the bypass methods: `setStageStarted`, `setStagePassed`, `setStageFailed`, `setStageAwaitingApproval`, `setRunStatus`, `upsertTask`, and `upsertCheck`.

Repository methods such as `updateStageRunStatus` can remain implementation details because repositories persist aggregate state; they must not be injected into runners or API handlers as business APIs.

**Alternatives considered:** Delete all service methods in one step. This is cleaner but creates a large migration cliff across runners, API approval handlers, Ralph execution, tests, and projections. Keep service methods public but document them as internal. This does not prevent future bypasses.

### D4: Replace Stage-Level Runner Results With Work Reports

Remove `StageRunResult.nextStage` and stop treating a runner return value as a stage decision. A runner should execute a single requested task or check and return a typed report:

```typescript
type WorkflowWork =
  | { kind: 'task'; stage: Stage; taskId: string }
  | { kind: 'check'; stage: Stage; checkName: string }
  | { kind: 'await-approval'; stage: Stage }
  | { kind: 'complete' }

type WorkReport =
  | { kind: 'task-result'; result: StageTaskResult }
  | { kind: 'check-result'; result: CheckResult }
  | { kind: 'approval-result'; approved: boolean; output?: unknown }
```

`BaseStageRunner` should no longer classify failures, schedule fix tasks, decide awaiting approval, or complete stages. It can remain as a common executor helper while the application service asks the aggregate for the next runnable work item after each report.

**Alternatives considered:** Keep `BaseStageRunner.run(ctx)` and ask it to call aggregate after internal loops. This reduces churn but preserves the runner as the hidden owner of task/check sequencing. Create one runner per task/check. This gives a clean boundary but is too large for P0.

### D5: Put Stage Definitions And Policies Next To The Domain

Represent built-in stage definitions as code data used by the aggregate, not as behavior scattered across runners. Each `StageDefinition` contains ordered task definitions, check definitions, and check failure policies.

Plan and Integrate have static tasks/checks seeded at workflow start. Build initially has no static task list; `MaterializeTasks(build, tasks)` populates it from `tasks.json`. Check contains `ai-review` as task work and `review-passed`, `merge-ready`, and `user-approval` as checks. Health checks remain read-only validators; any repair is modeled as a task scheduled by policy.

**Alternatives considered:** Continue deriving definitions from concrete runner methods such as `getChecks()` and `getCheckFailurePolicies()`. That keeps business rules hidden in execution code. Move definitions to a YAML DSL. That is explicitly out of scope and would add configuration complexity before the domain model is stable.

### D6: Store Side Effects As Task Metadata, Not Separate Entities

Do not add a standalone `SideEffect` table. Store irreversible external facts in task output/metadata. For `integrate:merge`, persist `targetBranch`, `baseSha`, `candidateHeadSha`, `landedSha`, and `rebased` on the task result. The aggregate uses those facts to set `freezePoint` and forbid automatic code-changing work after merge.

If the current schema lacks a dedicated metadata column, P0 can store structured metadata in the existing task `output` field with a stable shape and expose it through API projection.

**Alternatives considered:** Add a side-effect ledger. This could be useful later for audit, but it complicates P0 without enabling rollback. Encode side effects only in logs. Logs are evidence, not state, and UI/API would still need to infer delivery state.

### D7: Treat Projections As Consumers Of Aggregate Decisions

After each aggregate save, a projection component updates issue stage/status, `stage_states`, check suites, and emits SSE/workflow-log events. The projection must be rebuildable or lag-tolerant. API endpoints should prefer WorkflowRun state when available and use legacy data only for old runs without aggregate data.

Projection rules should be explicit:

- `WorkflowRun.currentStage` projects to `issues.stage`.
- terminal passed workflow projects to `issues.status = completed` and clears approval state.
- awaiting approval projects to issue approval state, stage-state approval, and `approval_requested` SSE.
- failed stages project failure status and reason while preserving task/check evidence.
- `stage_executions` remains an audit record and must not be read to decide current stage.

**Alternatives considered:** Stop writing legacy projections immediately. This risks breaking existing UI/API surfaces during migration. Keep legacy projections as co-equal truth. This preserves the current ambiguity.

### D8: Make Integrate Freeze A Domain Rule

When `integrate:merge` completes, `StageRun` records a freeze point using the merge task metadata. Once frozen, `recordCheckResult(integrate, health:integrate, failed)` must fail the stage/workflow with reason `post-merge-health-failed` and must not schedule `fix-integrate-health`, even if configuration says auto-fix is enabled.

Before the merge task completes, Integrate task failures remain normal task failures that stop later Integrate work. After merge, Mohist does not imply rollback; it exposes that delivery happened and manual intervention is required.

**Alternatives considered:** Rely on `IntegrateStageRunner.getCheckFailurePolicies()` returning no fix policy. This is not strong enough because configuration or future code could reintroduce the policy. Model freeze only in UI. That would not protect state transitions.

### D9: Compatibility For Existing Active Runs Is A Read-Repair Path

On loading an active run, the repository should tolerate partially seeded data and repair missing static Plan/Integrate tasks/checks idempotently. For Build, if no `workflow_tasks` exist but `tasks.json` is available, `materializeTasks` should populate rows before execution resumes. Existing `stage_states` or `stage_executions` may be used only to fill missing display evidence when no WorkflowRun data exists.

**Alternatives considered:** Force all active runs to restart. This violates compatibility expectations and risks losing visible progress. Backfill everything through a one-time migration only. A read-repair path is safer because some runs may be created by older code paths during rollout.

## Risks / Trade-offs

- [Risk] The refactor touches orchestration, runners, persistence, API approval, and UI projections at once. → Mitigation: migrate through adapters, keep legacy projection writes during P0, and remove bypass methods only after call sites use aggregate commands.
- [Risk] External side effects can succeed while aggregate save fails. → Mitigation: record task reports immediately after each side effect, keep operations idempotent where possible, and on resume detect already-applied side effects such as archived changes or merged branches before re-executing.
- [Risk] Checks may still perform hidden side effects. → Mitigation: keep check interfaces read-only, move convergence commits and repair work into explicit tasks or approval-command side effects, and add tests for check/fix boundaries.
- [Risk] Build currently uses `tasks.json.passes/error` as a resume signal. → Mitigation: use `tasks.json` only to materialize task definitions, then rely on `workflow_tasks` plus checkpoints for runtime progress; keep old fields as advisory evidence during migration.
- [Risk] Projection lag could make UI appear stale. → Mitigation: return WorkflowRun-backed stage-state responses when available and treat legacy projection updates as compatibility, not primary state.
- [Risk] Existing tests may assert `StageRunResult.nextStage` or `WorkflowRunService` setters. → Mitigation: add aggregate tests first, then update runner/API tests to assert decisions and projections.

## Migration Plan

1. Add domain model types and pure unit tests for aggregate invariants: start workflow, stage admission, single active stage, task order, task/check boundary, approval, fix scheduling, stage completion, Integrate freeze, and post-merge health failure.
2. Add repository mapping from existing WorkflowRun relational rows to aggregate snapshots and a transactional `save(WorkflowRun)` path. Keep existing row-level repository methods private or package-local implementation details.
3. Add `WorkflowApplicationService` with commands for start, materialize tasks, complete task, record check result, approve/reject stage, and resume next work. Wire start paths to create/reuse WorkflowRun through this service.
4. Refactor `BaseStageRunner` and concrete runners so they execute requested work and return reports. Move check classification, fix scheduling, approval state, and stage completion decisions into `StageRun`.
5. Refactor `WorkflowEngine` to loop on aggregate `nextWork()` and stop using `StageRunResult.nextStage` or direct issue stage updates for progression.
6. Refactor Ralph/Build integration to materialize `tasks.json` into `workflow_tasks` before execution and report each task completion/failure/skipped result to the aggregate.
7. Refactor Integrate to report `integrate:spec-sync`, `integrate:archive-change`, `integrate:merge`, and `health:integrate` facts to the aggregate, including merge metadata and freeze behavior.
8. Move projection updates into a dedicated projection component applied after aggregate save. Update issue stage/status, approval state, `stage_states`, check suites, SSE, and logs from decisions.
9. Update API and UI data paths to read WorkflowRun-backed stage state when available. Preserve legacy fallbacks for runs without WorkflowRun rows.
10. Remove or make inaccessible public bypass methods on `WorkflowRunService`; update tests to fail if runners/API code call lifecycle setters directly.

Rollback strategy: keep the existing tables and legacy projections intact during rollout. If aggregate execution fails unexpectedly, feature-gate the new application service and route execution back to the previous runner path for new runs while preserving already-written WorkflowRun rows as display evidence. Do not attempt to roll back external side effects such as archive or merge.

## Open Questions

- Should P0 add explicit `failure_reason`, `freeze_point`, or `metadata` columns, or encode those facts in existing `output` fields until a later schema cleanup?
- Should approval convergence commits be modeled as an explicit `check:converge-approval-snapshot` task, or as a side effect of the approval-request command with a recorded task-style output?
- Should repeated check attempts remain collapsed in `workflow_checks.run_count/output` for P0, or should the aggregate expose transient in-memory events so UI can show repeated attempts without adding `workflow_check_runs` yet?
