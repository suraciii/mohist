## Context

`StageStateService` currently mixes two concerns in one persisted `stage_tasks` list:

- legacy seeded placeholder tasks such as `read-context`, `design-solution`, `merge-branch`
- real workflow tasks reported by stage runners such as `proposal`, `specs`, `fix-build-health`, `repair-plan-artifacts`

`GET /api/issues/:number/stage-state` returns those rows directly, and both `PipelineView` and `TaskProgressPanel` render that response as the stage's user-visible task list. This is why Plan can show both placeholder tasks and real artifact tasks at the same time.

Current constraints:

- We should not redesign the workflow engine or introduce a new workflow DSL in this issue.
- Existing persistence and stage runners already provide enough raw signals to build a better projection: `stage_tasks`, `stage_checks`, `stage_executions.task_results`, `tasks.json`, approval state, and runtime task events.
- The fix should be incremental and schema-light: prefer a UI-facing projection over a database rewrite.
- The same canonical task/check view must feed both Issue Detail surfaces so they cannot diverge again.

The change therefore focuses on redefining the semantics of the stage-state API response from "raw stored rows" to "canonical user-visible workflow stage view".

## Goals / Non-Goals

**Goals:**

- Return exactly one user-visible task list and one user-visible check list per stage.
- Ensure every visible task is a real workflow execution unit that ran, is running, is queued to run, or was added later by workflow policy.
- Remove obsolete placeholder tasks from Plan and other stages when those placeholders are not executable workflow tasks.
- Preserve runtime-added repair/retry/rebase tasks in the same task list, with explanation metadata.
- Keep checks separate from tasks and keep logs/session evidence in task or check details, not as top-level list entries.
- Keep `PipelineView` and `TaskProgressPanel` on the same response model.

**Non-Goals:**

- No rewrite of the workflow engine or stage runner control flow.
- No migration to a new workflow definition format.
- No attempt to model every internal session event or reasoning step as a task.
- No requirement to redesign check execution semantics or approval semantics beyond presentation and projection.
- No requirement to fully normalize historical database rows beyond what is needed for correct current UI output.

## Decisions

### D1: Keep storage, change projection semantics

The primary change will happen in `StageStateService` and the `stage-state` API contract, not in the database schema.

Instead of returning `stage_tasks` rows as-is, the service will build a `WorkflowStageView`-style projection for each stage:

- stage metadata from `stage_states`
- task list from real executable task evidence only
- check list from `stage_checks`
- approval state from `stage_states.approval_*`

This keeps the implementation small, avoids data migrations, and fixes both UI surfaces at once because they already share `useIssueStageState()`.

**Alternatives considered:** rewrite `stage_tasks` schema immediately to remove placeholders forever. Rejected for this issue because it would expand scope into migration, historical compatibility, and runner persistence changes that are not necessary to fix the user-visible bug.

### D2: Introduce a UI-facing task view with reason metadata

`StageTaskState` is currently too close to raw storage. The response should evolve toward a view model that keeps the existing basics and adds explanation fields for runtime-added tasks.

Planned shape:

- keep: `taskId`, `title`, `status`, `order`, `attempts`, `duration`, `artifacts`, `output`, timestamps
- add: `reason?: string`
- add: `causedBy?: { type, checkName?, taskId?, message? }`
- remove from user-visible meaning: `source: 'static' | 'dynamic'` as a primary concept

`source` may remain internally or on the wire for compatibility, but the UI must not present it as a category. The canonical explanation becomes human-readable reason metadata such as `Added after Tasks valid failed`.

**Alternatives considered:** keep `source` and teach the UI to hide only known placeholder IDs. Rejected because that would encode brittle stage-specific exceptions in the frontend and would not solve runtime explanation for repair/rebase tasks.

### D3: Define real-task projection rules per stage

The projection should be rule-based and conservative: only include tasks backed by executable workflow evidence.

Rules by stage:

- Plan: include runner-reported artifact tasks (`proposal`, `specs`, `design`, `tasks`, `self-review`) and any real repair/health tasks (`repair-plan-artifacts`, `fix-plan-health`). Exclude legacy placeholders such as `read-context` and `design-solution`.
- Build: include `tasks.json` tasks plus real runtime repair tasks such as `fix-build-health`.
- Check: include real review and repair tasks such as `ai-review`, `fix-review-findings`, `repair-merge`, and any rerun task instances that actually execute.
- Integrate: include real integrate tasks that execute in the runner, plus runtime additions like rebase/conflict-repair when they occur.

Implementation-wise, the backend will compute the visible task set from a combination of:

- existing `stage_tasks` rows when their IDs map to known real tasks
- `stage_executions.task_results`
- `tasks.json` for Build
- explicit metadata for repair/retry/rebase tasks where available

If a row exists only because `seedStaticTasks()` inserted an old placeholder and there is no matching executable evidence, it is filtered out of the API response.

**Alternatives considered:** delete placeholder rows during stage start. Rejected for first pass because filtering at projection time is safer for active issues and legacy data.

### D4: Stop seeding obsolete placeholder tasks for new stage runs

Projection filtering fixes the immediate UI bug, but `ensureStage()` and legacy projection still write noise into `stage_tasks`. The design should also remove or narrow `seedStaticTasks()` so new stage runs stop persisting non-executable placeholders.

Preferred direction:

- remove Plan placeholder seeding entirely
- remove Check/Integrate placeholder seeding if those rows are not executable tasks
- keep stage row creation independent from task seeding

For stages that need an initial user-visible list before execution evidence exists, seed only tasks that are actual runner tasks, not conceptual placeholders.

**Alternatives considered:** leave seeding untouched and rely only on filtering forever. Rejected because it preserves misleading state in storage and makes future projection logic harder to reason about.

### D5: Keep checks as a separate list and surface approval alongside checks

Checks stay in `checks[]`, and approval remains stage decision state rather than a task. The UI can render approval inline near the checks section, but the data model should continue to separate:

- task execution units
- read-only checks
- approval/decision state

This matches the product invariant that check evidence and approval should not be promoted into fake tasks.

**Alternatives considered:** represent `user-approval` as a synthetic task to simplify UI layout. Rejected because it reintroduces the exact category confusion this issue is trying to remove.

### D6: Use backend-owned task title and reason mapping

Human-facing titles and reason strings should be produced by the backend projection layer, not reconstructed in multiple frontend components.

This includes:

- stable titles for Plan artifact tasks (`Write proposal`, `Write specs`, `Write design`, `Write implementation tasks`, `Self-review plan`)
- stable labels for repair and health tasks (`Repair plan artifacts`, `Fix build health`, `Fix review findings`)
- reason strings derived from workflow cause (`Added after Tasks valid failed`, `Added because target branch moved`)

Centralizing these mappings in backend projection avoids drift between `PipelineView` and `TaskProgressPanel` and keeps tests focused on one contract.

**Alternatives considered:** let the frontend map raw task IDs to titles/reasons. Rejected because that would duplicate workflow knowledge in the UI and make API consumers inconsistent.

### D7: Extend existing tests around shared stage-state consumption

Regression coverage should validate the projection contract directly and then verify both UI surfaces consume it consistently.

Test layers:

- backend tests for `StageStateService` projection filtering and reason metadata
- API-level tests for `GET /api/issues/:number/stage-state` semantics where present
- frontend tests extending `stage-state-consistency.test.tsx` with:
  - Plan placeholder rows plus real Plan tasks -> only real tasks render
  - runtime repair task -> renders with reason metadata
  - task/check separation -> checks do not appear in task lists

**Alternatives considered:** only add frontend snapshot tests. Rejected because the bug originates in backend projection semantics, and UI-only tests would not protect the contract.

## Risks / Trade-offs

- [Projection rules become stage-specific and drift from runner behavior] → Keep stage task ID/title mappings close to workflow code and cover them with explicit tests per stage.
- [Historical issues contain incomplete evidence, so filtering may hide tasks that older runs once displayed] → Prefer evidence from `stage_executions`, `tasks.json`, and persisted real task rows; when evidence is absent, show fewer tasks rather than fabricated ones.
- [Changing `stage-state` response semantics could surprise existing consumers] → Keep field names stable where possible, add metadata in a backward-compatible way, and update all known in-repo consumers together.
- [Reason metadata may be unavailable for some legacy repair tasks] → Allow `reason` / `causedBy` to be optional and fill it only where the current workflow can derive it reliably.
- [Leaving placeholder rows in old databases can confuse future maintainers] → Stop seeding obsolete placeholders for new runs and document that API projection, not raw rows, is the source of truth.

## Migration Plan

1. Add backend projection helpers in `StageStateService` that build canonical stage task/check views and filter obsolete placeholder tasks.
2. Extend task view types in backend and frontend to include optional `reason` / `causedBy` metadata while preserving existing fields needed by the UI.
3. Update `GET /api/issues/:number/stage-state` to return the projected view instead of raw `stage_tasks` rows.
4. Remove or narrow placeholder seeding for new stage runs so fresh data matches the new semantics.
5. Update `PipelineView` and `TaskProgressPanel` to render the refined task model, including reason/evidence details, while continuing to share `useIssueStageState()`.
6. Add regression tests for Plan placeholder filtering, runtime repair task visibility, and shared rendering consistency.
7. Rollback strategy: if the new projection causes regressions, revert the API projection and UI type changes together; no schema rollback is required because the design avoids destructive migrations.

## Open Questions

- For Integrate, which exact task IDs should be considered the canonical user-visible task sequence in the first pass: only persisted runner task IDs, or also selected integration step records if some steps are not currently persisted as tasks?
- For Check reruns, do we want repeated review tasks to appear as distinct task entries (`ai-review`, `re-run-ai-review`) or as one task with incremented attempts in the first version? The issue examples allow either, but the projection contract should choose one representation consistently.
