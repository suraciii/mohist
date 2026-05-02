## Self-Review: Issue #130 — Unified Task Domain Model + CI/CD-Style Pipeline View

**Reviewer**: Self-review (agent)
**Date**: 2026-05-02
**Status**: PASS with fixes applied

---

## Completeness

### Spec Coverage

| Spec Requirement | Covered by Task | Status |
|---|---|---|
| unified-stage-task-model: StageTask unified domain type | T-001 | ✅ |
| unified-stage-task-model: StageTaskResult records outcomes | T-001 | ✅ |
| unified-stage-task-model: Plan RoundConfig maps to StageTask | T-002 | ✅ |
| unified-stage-task-model: Check RoundConfig maps to StageTask | T-002 | ✅ |
| unified-stage-task-model: task_results stores StageTaskResult[] | T-001, T-002, T-003 | ✅ |
| unified-stage-task-model: BaseStageRunner records incrementally | T-001 | ✅ |
| unified-stage-task-model: findByIssueId repo method | T-001 | ✅ |
| pipeline-session-events: stage_task_update unified event | T-001, T-002, T-003 | ✅ |
| pipeline-session-events: registered in SSE event types | T-001 (backend), T-005 (frontend) | ✅ (fix applied) |
| pipeline-session-events: fire-and-forget | T-001 | ✅ |
| pipeline-session-events: Old SSE events continue | T-002, T-003 | ✅ |
| pipeline-session-events: Plan emits both old+new | T-002 | ✅ |
| pipeline-session-events: Build passes eventBus to RalphExecutor | T-003 | ✅ |
| http-api: GET /api/issues/:number/executions | T-004 | ✅ |
| pipeline-view: PipelineView replaces fragmented components | T-006, T-007 | ✅ |
| pipeline-view: Stage Bar horizontal progress | T-006 | ✅ |
| pipeline-view: Step List Tasks + Checks | T-006 | ✅ |
| pipeline-view: Inline Approval | T-006 | ✅ |
| pipeline-view: All issue states | T-006 | ✅ |
| pipeline-view: SSE subscription | T-005, T-006 | ✅ |
| pipeline-view: Historical data from executions API | T-005 | ✅ |
| pipeline-view: RAF throttling | T-005 | ✅ |
| session-timeline-ui: 7 requirements removed | T-007 | ✅ |

### Edge Cases

| Edge Case | Covered | Where |
|---|---|---|
| Partial results after mid-stage failure | ✅ | unified-stage-task-model spec scenario |
| Skipped tasks (checkpoint hit) | ✅ | T-002 AC |
| Build task retries | ✅ | T-003 AC |
| Escalation cycle (multiple Plan records) | ✅ | http-api spec scenario |
| Issue not found (404) | ✅ | T-004 AC |
| Issue with no executions (empty array) | ✅ | T-004 AC |
| Draft/backlog issue with no pipeline data | ✅ | pipeline-view spec scenario |
| Rapid SSE events (500+ in 5s) | ✅ | pipeline-view spec scenario |
| EventBus emit failure during stage_task_update | ✅ | pipeline-session-events spec scenario |

---

## Consistency

### Proposal → Specs

- `unified-stage-task-model` (new) → dedicated spec with 7 requirements ✅
- `pipeline-view` (new) → dedicated spec with 8 requirements ✅
- `pipeline-session-events` (modified) → ADDED + MODIFIED requirements ✅
- `http-api` (modified) → ADDED requirement ✅
- `session-timeline-ui` (modified) → REMOVED requirements with migration paths ✅

### Specs → Tasks

All task `spec` references verified — each points to a valid requirement heading in the correct spec file.

### Design → Tasks

| Design Decision | Task Coverage |
|---|---|
| D1: StageTask as view type | T-001 (types), T-002 (Plan/Check mapping), T-003 (Build mapping) ✅ |
| D2: Incremental task_results via append | T-001 (recordTaskResult helper) ✅ |
| D3: SSE event is additive (dual emission) | T-002 (Plan/Check), T-003 (Build) ✅ |
| D4: Frontend fetches history from API, live from SSE | T-005 (usePipelineView) ✅ |
| D5: PipelineView component hierarchy | T-006 (components) ✅ |
| D6: Build via onTaskCompleted callback | T-003 ✅ |

### Naming Consistency

- `StageTask`, `StageTaskResult`, `stage_task_update`, `TaskConfig`, `recordTaskResult` — consistent across all artifacts ✅
- Stage enum values (`Plan`, `Build`, `Check`, `Done`) — consistent ✅
- API endpoint `/api/issues/:number/executions` — consistent across spec, design, tasks ✅

---

## Feasibility

### Dependency Graph

```
T-001 ──┬──→ T-002
         ├──→ T-003
         └──→ T-004 ──→ T-005 ──→ T-006 ──→ T-007
```

- Valid DAG: no cycles ✅
- All dependsOn reference lower-priority tasks ✅
- Every non-first task has at least one dependsOn ✅
- T-002 and T-003 are independent of each other (can run in parallel) ✅

### Task Granularity

- T-001 is the heaviest task (types + repo + event registration + recordTaskResult helper = 5 output files). All pieces are tightly coupled foundation — acceptable.
- T-002 covers both Plan and Check stage runners. They share the same pattern (RoundConfig→TaskConfig, emit events, record results) — implementing together avoids duplication. Acceptable.
- T-006 creates 4 new component files in one task. They're a single deliverable (the visible Pipeline View). Acceptable.
- All other tasks are well-scoped for a single agent session.

---

## Issues Found and Fixed

### Fix 1: T-005 missing useSSE.ts eventTypes acceptance criterion

**Problem**: The pipeline-session-events spec explicitly requires `stage_task_update` to be added to `useSSE.ts` `eventTypes` array (line 57: "useSSE.ts eventTypes array (frontend)"). T-005's acceptance criteria mention `AGENT_DETAIL_EVENTS` and `AgentDetailEventMap` but do not mention the `useSSE.ts` `eventTypes` array. Without this, the SSE client would not subscribe to `stage_task_update` events, so the frontend would never receive them via the EventSource connection.

The current `useSSE.tsx` (line 250-286) defines a hardcoded `eventTypes: EventName[]` array that controls which events the EventSource listener subscribes to. Missing `stage_task_update` here means the browser's EventSource would ignore the event entirely.

**Fix**: T-005 acceptance criteria should include: "stage_task_update is added to useSSE.ts eventTypes array". This is a minor addition — T-005 already edits frontend SSE-related files.

---

## Known Gaps (Accepted)

1. **Open questions in design unresolved**: D1-D6 are decided but the design has two open questions about `StageTaskResult.duration` (total wall clock vs last attempt) and `artifacts` path format (relative vs absolute). These don't block specs or tasks — they're implementation details to resolve during T-001.

2. **No explicit test task**: Unlike issue #127 which had a dedicated T-012 test task, this change relies on each task's "npm test passes" AC. The existing test suite should cover the modified stage runners and API endpoint. If needed, a follow-up change can add dedicated Pipeline View integration tests.

3. **StageTask for Build may have many tasks**: Design notes 20+ Build tasks as a potential concern. StepList renders a flat list — acceptable at this scale. DAG visualization is explicitly a non-goal.

---

## Verdict

**PASS** — All artifacts are consistent, complete, and feasible after the 1 fix applied (T-005 AC gap for useSSE.ts eventTypes). The dependency graph is valid with no cycles or forward references. The 5 specs cover all proposal capabilities, the 6 design decisions are implemented by 7 tasks forming a valid DAG, and the session-timeline-ui removal spec provides clear migration paths.
