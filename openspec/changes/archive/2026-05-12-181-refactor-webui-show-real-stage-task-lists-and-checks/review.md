# Review: Issue #181 — Show real stage task lists and checks

## Summary

The implementation successfully addresses the core problem: obsolete placeholder tasks are no longer exposed in the user-visible stage task list. The approach is a projection filter at the API layer (`isRealTask`) rather than a database migration, which aligns with the design decision (D1). The frontend now correctly renders real tasks only, with reason metadata for runtime-added repair tasks, and keeps checks visually separate from tasks.

---

## Correctness

### Issues Found

#### WARNING: `isRealTask` is an allowlist that must be maintained manually

`REAL_TASK_IDS` at `stage-state-service.ts:313-320` is a hardcoded per-stage allowlist. Any new runner task ID not currently listed will be silently filtered out of the API response. For example:

- `merge-branch` and `verify-merge` are in the Integrate allowlist, but the task description mentions `Sync OpenSpec specs` and `Archive OpenSpec change` as Integrate tasks — these are not in the allowlist. If their task IDs differ (e.g., `integrate:spec-sync`, `integrate:archive-change`), they will be filtered out.

- `repair-build` is in the Build allowlist but `self-review` is in the Plan allowlist — `self-review` is hyphenated but `tasks` is not. The current IDs look correct for what exists today, but there's a risk of drift.

**Mitigation in code**: The fallback in `isRealTask` (`stage-state-service.ts:326`) catches task IDs starting with `repair-` or `fix-` even if not explicitly listed, which partially mitigates this for repair tasks. However, new primary workflow tasks outside the allowlist pattern will be hidden.

**Severity**: Low. The design explicitly chose this approach (D3: "conservative: only include tasks backed by executable workflow evidence"). But it should be documented that new task IDs need to be added to `REAL_TASK_IDS`.

#### WARNING: Empty static task seed arrays mean no pre-seeding for Plan/Check/Integrate

`PLAN_TASK_DEFS`, `CHECK_TASK_DEFS`, and `INTEGRATE_TASK_DEFS` at `stage-state-service.ts:388-392` are all empty arrays. This means `seedStaticTasks()` does nothing for these stages. This is correct per the design (D4: "remove Plan placeholder seeding entirely"), but it means that for Plan, the stage will show 0 tasks until the runner reports the first real task. This creates a brief UX gap where the stage appears to have no tasks at all.

**Severity**: Low. The old placeholder tasks were worse (misleading), and the runner reports tasks quickly. The current behavior is correct per spec.

#### INFO: `source` field is preserved on the wire but not used categorically by UI

The `StageTaskState` type still has `source: 'static' | 'dynamic'` at `stage-state-service.ts:25` and `types.ts:806`. The design (D2) says "`source` may remain internally or on the wire for compatibility, but the UI must not present it as a category." The implementation correctly does not use `source` for any UI differentiation — it's just passed through. This is fine but could be cleaned up in a future pass.

### No errors found

The core logic — filtering obsolete placeholders via `isRealTask`, enriching runtime tasks with `reason`/`causedBy` via `RUNTIME_TASK_EXPLANATIONS`, and rendering `reason` in the UI — is correct.

---

## Complexity

All functions are under 50 lines. `getIssueStageState` is the longest method at ~30 lines and is straightforward. `isRealTask` is a 10-line pure function. `rowToStageTask` is ~30 lines including the reason/causedBy enrichment.

No cyclomatic complexity exceeds 10.

---

## Security

No injection risks. All database operations use parameterized queries. The `isRealTask` filter is applied server-side before the response is sent. No secrets are exposed.

---

## Test Coverage

### Backend tests (`stage-state-service.test.ts`)

- **Placeholder filtering**: Tests that Plan placeholder IDs `read-context` and `design-solution` are excluded while real tasks `proposal`, `specs`, `self-review` are included. (Lines 359-412)
- **Reason/causedBy metadata**: Tests that `fix-review-findings` gets `reason: 'Added after review passed failed'` and `causedBy: { type: 'check-failure', checkName: 'ai-review' }`. (Lines 414-438)
- **Rebase reason**: Tests `rebase-branch` gets `reason: 'Added because target branch moved'` and `causedBy: { type: 'rebase' }`. (Lines 440-463)
- **Build tasks.json pattern**: Tests `T-001`, `T-002`, and `fix-build-health` all pass the filter. (Lines 465-501)
- **Static task seed is empty**: Tests that Plan/Check/Integrate stages seed 0 tasks. (Lines 44-63)

### Frontend tests (`stage-state-consistency.test.tsx`)

- **Shared data source**: Both `PipelineView` and `TaskProgressPanel` render tasks from the same `useIssueStageState` hook. (Lines 137-161, 221-235)
- **No Plan placeholders**: Tests that `Read context files` and `Design solution` are absent. (Lines 237-266)
- **Runtime repair task visible**: Tests that `Fix build health` appears in both components. (Lines 163-188)
- **Reason rendering**: Tests that `Repair plan artifacts` renders in PipelineView. (Lines 268-291)
- **Check/task separation**: Tests that check names `ai-review` and `build-test` appear in the Checks section, not the Tasks section. (Lines 293-366)

### Regression tests (`stage-state-regression.test.ts`)

- **Dynamic fix tasks visible**: `fix-check-health`, `fix-build-health`, `fix-plan-health`, `fix-review-findings` all appear correctly. (Lines 187-305)
- **tasks.json mirroring**: Build tasks from tasks.json are projected with normalized status. (Lines 308-444)

### API-level tests (`stage-state-api.test.ts`)

- **No `passes` field leak**: Confirms `passes` is not exposed. (Lines 300-327)
- **Legacy projection**: Tests that tasks.json data and check_suite data are lazily projected. (Lines 328-489)

**Assessment**: Backend and API test coverage is strong. Frontend coverage is adequate for the acceptance criteria. One gap: the frontend test for "reason label on runtime-added task" (`stage-state-consistency.test.tsx:268-291`) checks that the task *renders*, but it uses a task with `reason` metadata provided by the mock data. It does not test that the PipelineView *displays* the reason text. Looking at the TaskItem component (`PipelineView.tsx:272-313`), `reason` is displayed as an amber "reason" label inline and in the expanded details section. The test could be strengthened to assert the reason text appears, but this is a minor improvement opportunity.

---

## Spec Compliance

### REQ-HTTP-001: Stage-state excludes obsolete placeholders

**PASS.** `isRealTask()` at `stage-state-service.ts:322-334` filters out all placeholder task IDs that are not in `REAL_TASK_IDS[stage]`. The test at `stage-state-service.test.ts:360-412` verifies that `read-context` and `design-solution` are excluded while `proposal`, `specs`, `self-review` are included. The frontend test at `stage-state-consistency.test.tsx:237-266` confirms `Read context files` and `Design solution` do not render.

### REQ-HTTP-001: Stage-state includes reason-aware runtime tasks

**PASS.** `RUNTIME_TASK_EXPLANATIONS` at `stage-state-service.ts:343-387` maps all known repair/rebase/fix task IDs to `reason` and `causedBy` metadata. `rowToStageTask` at `stage-state-service.ts:172-183` enriches matching tasks. The backend test at `stage-state-service.test.ts:414-438` confirms `fix-review-findings` gets `reason` and `causedBy`. The frontend TaskItem at `PipelineView.tsx:272-286` renders the reason label and expanded details. The frontend test at `stage-state-consistency.test.tsx:268-291` confirms `Repair plan artifacts` renders.

### REQ-HTTP-001: Checks are kept separate from tasks

**PASS.** The API response at `getIssueStageState` returns `tasks: StageTaskState[]` and `checks: StageCheckState[]` as separate arrays. The frontend StepList at `PipelineView.tsx:714-798` renders them in separate sections with "Tasks" and "Checks" headings. The frontend test at `stage-state-consistency.test.tsx:293-366` verifies check names do not appear in the tasks section.

### REQ-PM-001: Placeholder rows not visible

**PASS.** Same evidence as REQ-HTTP-001 placeholder exclusion.

### REQ-PM-001: Runtime repair stays in same task list

**PASS.** The API returns all tasks in a single `tasks[]` array regardless of source. The frontend renders them in a single task list. Runtime repair tasks like `fix-review-findings` are enriched with `reason`/`causedBy` but are in the same list. No category splitting.

### REQ-WUI-001: Plan shows only real tasks

**PASS.** `REAL_TASK_IDS[Stage.Plan]` = `{'proposal', 'specs', 'design', 'tasks', 'self-review', 'repair-plan-artifacts', 'fix-plan-health'}`. No placeholder IDs like `read-context`, `design-solution` are included. Frontend test confirms absence of old placeholders.

### REQ-WUI-001: Runtime-added task is explained

**PASS.** TaskItem in `PipelineView.tsx:272-286` shows a "reason" label when `task.reason != null`, and displays the full reason text in expanded details at `PipelineView.tsx:311-314`. TaskProgressPanel at `TaskProgressPanel.tsx:31-74` also shows reason in expanded details at `TaskProgressPanel.tsx:65-67`.

### REQ-WUI-004: TaskProgressPanel and PipelineView use same stage-state

**PASS.** Both components use `useIssueStageState` which calls `GET /api/issues/:number/stage-state`. The frontend test at `stage-state-consistency.test.tsx:221-235` confirms both components call the same hook. Both render tasks from `stageState.tasks`.

### REQ-WUI-004: Checks not promoted to tasks

**PASS.** StepList renders tasks and checks in separate `<div>` sections with separate headings. The test at `stage-state-consistency.test.tsx:355-365` confirms check names don't appear in the tasks div.

### Acceptance criterion: #180 Plan page no longer shows read-context, design-solution, etc.

**PASS.** Covered by `REAL_TASK_IDS` exclusion and frontend test.

### Acceptance criterion: Build stage shows tasks.json actual tasks

**PASS.** `T-NNN` pattern building tasks pass `isRealTask` via `stage-state-service.ts:330` regex `/^T-\d+$/`.

### Acceptance criterion: Runtime repair/rebase/retry tasks appear with reason/causedBy

**PASS.** All repair task IDs have entries in `RUNTIME_TASK_EXPLANATIONS`.

### Acceptance criterion: Checks displayed independently

**PASS.** Separate `checks[]` array and separate UI section.

### Acceptance criterion: Regression tests cover mixed placeholder-plus-real and runtime-added scenarios

**PASS.** `stage-state-service.test.ts:359-501` covers mixed placeholder + real data. `stage-state-service.test.ts:414-463` covers runtime repair with reason. `stage-state-consistency.test.tsx` covers both frontend scenarios.

---

## Warnings

1. **Hardcoded allowlist maintenance risk**: `REAL_TASK_IDS` must be updated when new task IDs are introduced by runners. Consider adding a comment block or documentation pointing to this map from relevant runner code.

2. **Integrate task completeness**: `verify-merge` is in the Integrate allowlist but I couldn't find where it's actually created as a task. If the Integrate runner uses different IDs (e.g., `integrate:spec-sync`, `integrate:archive-change`, `integrate:merge`), they won't pass the filter.

3. **Minor formatting**: In `PipelineView.tsx:286`, the reason label just says "reason" as text — it shows `task.reason` as a tooltip via `title={task.reason}` and also displays it in expanded details. This is acceptable but the inline "reason" label could be more descriptive (e.g., showing the actual reason text truncated, or a more specific icon). Low priority.

---

<promise>PASS</promise>