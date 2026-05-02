## Self-Review: Issue #127 — Replace Gate with Check Model

**Reviewer**: Self-review (agent)
**Date**: 2026-05-02
**Status**: PASS with fixes applied

---

## Completeness

### Spec Coverage

| Spec Requirement | Covered by Task | Status |
|---|---|---|
| unified-check-model: BaseStageRunner execution loop | T-002, T-004, T-005, T-006 | ✅ |
| unified-check-model: Check interface with Reaction | T-001, T-002 | ✅ |
| unified-check-model: user-approval is a Check | T-003, T-004, T-005 | ✅ |
| unified-check-model: Serial check evaluation | T-002 | ✅ |
| unified-check-model: StageRunResult no gate fields | T-001, T-007 | ✅ |
| unified-check-model: Uniform persistence | T-009, T-011 | ✅ |
| unified-check-model: WorkflowEngine no gate logic | T-007, T-008 | ✅ |
| pipeline-model: 4-stage pipeline | T-004, T-005, T-006, T-007 | ✅ |
| pipeline-model: CHECK failure loops back | T-004 (reactions), T-012 (tests) | ✅ |
| pipeline-model: REMOVED Gate/Job/Config | T-010 (deletion), T-007 (engine) | ✅ |
| workflow-definition: Plan stage behavior | T-005 | ✅ |
| workflow-definition: Build stage behavior | T-006 | ✅ |
| workflow-definition: Check stage behavior | T-004 | ✅ |
| workflow-definition: Review stage REMOVED | Implicit (no task creates it) | ✅ |
| workflow-definition: Backward compatibility | T-005 AC, T-006 AC, T-012 AC | ✅ (added) |
| ralph-task-execution: Task loop via BaseStageRunner | T-006 | ✅ |
| ralph-task-execution: Task failure retry | T-006 (preserves RalphExecutor) | ✅ |
| ralph-task-execution: Loop back from check to build | T-004 (escalate reaction), T-012 | ✅ |

### Edge Cases

| Edge Case | Covered | Where |
|---|---|---|
| Non-OpenSpec issues (no openspec/changes/) | ✅ | T-005 AC, T-006 AC, T-012 test added |
| Done stage (no StageRunner exists yet) | ⚠️ Deferred | Design open question: placeholder deferred |
| ask-user reaction must call setApprovalState | ✅ (added) | T-002 AC added |
| clearApprovalState on Done stage | ✅ (fixed) | T-007 AC relaxed |

---

## Consistency

### Proposal → Specs

- `unified-check-model` (new) → dedicated spec with 8 requirements ✅
- `pipeline-model` (modified) → MODIFIED + REMOVED requirements ✅
- `workflow-definition` (modified) → MODIFIED requirements for all 4 stages ✅
- `ralph-task-execution` (modified) → MODIFIED requirements ✅

### Specs → Tasks

All task `spec` references verified — each points to a valid requirement heading in the correct spec file.

### Design → Tasks

| Design Decision | Task Coverage |
|---|---|
| D1: BaseStageRunner abstract class | T-002 ✅ |
| D2: Reaction on Check | T-001 (type), T-002 (dispatch) ✅ |
| D3: Absorb AcpRoundRunner into Plan | T-005 ✅ |
| D4: Keep RalphExecutor in Build | T-006 ✅ |
| D5: user-approval reads approvalState | T-003 ✅ |
| D6: stage_executions table | T-009 ✅ |
| D7: Simplified result types | T-001 ✅ |
| D8: Simplified engine | T-007 ✅ |

### Naming Consistency

- `BaseStageRunner`, `ReactionConfig`, `UserApprovalCheck`, `stage_executions`, `StageExecutionRepo` — consistent across all artifacts ✅
- Stage enum values (`Plan`, `Build`, `Check`, `Done`) — consistent ✅

---

## Feasibility

### Dependency Graph

```
T-001 ──┬──→ T-002 ──┬──→ T-004 ──┐
        │             ├──→ T-005 ──┤──→ T-007 ──→ T-008 ──→ T-010 ──┐
        ├──→ T-003 ──┘             │                                   ├──→ T-012
        └──→ T-009 ────────────────┘──→ T-011 ───────────────────────┘
             └──→ T-006 ───────────┘
```

- Valid DAG: no cycles ✅
- All dependsOn reference lower-priority tasks ✅
- Every non-first task has at least one dependsOn ✅

### Task Granularity

- T-005 (Plan migration) is the heaviest task (5 new checks + absorb AcpRoundRunner + migrate runner). Risk of exceeding one agent session. Mitigated by detailed notes. Acceptable.
- T-004 (Check migration) is also substantial (2 check updates + absorb AcpRoundRunner from AiReviewCheck + migrate runner). Acceptable.
- All other tasks are well-scoped for a single agent session.

---

## Issues Found and Fixed

### Fix 1: T-001 gateRequired removal timing

**Problem**: T-001 AC said "PipelineResult has no gateRequired field" — but AgentRunnerService references `result.gateRequired` and isn't updated until T-008. Removing the field in T-001 would break compilation.

**Fix**: Changed AC to keep `gateRequired` temporarily (set to `false` everywhere). Removal moved to T-007 when engine is simplified.

### Fix 2: T-002 missing setApprovalState in ask-user reaction

**Problem**: When the ask-user reaction fires, nothing calls `issueRepo.setApprovalState(id, { status: 'awaiting' })`. Without this, the approval API (`POST /approve`) can't resolve the pending state, and AgentRunnerService can't detect that the pipeline is paused at user-approval.

**Fix**: Added AC: "ask-user reaction calls issueRepo.setApprovalState(id, { status: 'awaiting' }) before emitting approval_requested event".

### Fix 3: T-007 clearApprovalState too strict

**Problem**: AC said "No setApprovalState or clearApprovalState calls in WorkflowEngine" — but `clearApprovalState` is needed on Done stage for cleanup (reset approval state after pipeline completes). Removing both would break the approval state lifecycle.

**Fix**: Relaxed AC to: "No setApprovalState calls in WorkflowEngine" (moved to BaseStageRunner). "clearApprovalState remains in WorkflowEngine for Done stage cleanup only." Added explicit AC for PipelineResult.gateRequired field removal.

### Fix 4: T-004 AiReviewCheck boundary unclear

**Problem**: Description said "absorb the AcpRoundRunner usage from AiReviewCheck" but didn't clarify what AiReviewCheck becomes after the ACP session logic moves to executeTasks().

**Fix**: Clarified: ACP session logic moves to CheckStageRunner.executeTasks(). AiReviewCheck becomes a data-only check that parses review.md output (read file, parse verdict). No more ACP calls inside the check.

### Fix 5: Backward compatibility missing from tasks

**Problem**: The workflow-definition spec requires "The system SHALL support traditional workflow for issues without Change artifacts" but no task had an explicit AC for this.

**Fix**: Added backward compat ACs to T-005, T-006, and T-012 (non-OpenSpec issue test case).

### Fix 6: T-012 missing non-OpenSpec test

**Problem**: Test task didn't cover the backward compatibility scenario.

**Fix**: Added test case: "New test: non-OpenSpec issue (no openspec/changes/) still completes full pipeline".

---

## Known Gaps (Accepted)

1. **Done stage runner**: No task creates a `DoneStageRunner`. The design recommends deferring this. Done stage handling stays in WorkflowEngine's loop exit. Not blocking — the engine already handles `Stage.Done` correctly.

2. **API layer updates**: `POST /approve` and `POST /reject` endpoints in `api/issues.ts` still reference `approvalState` directly. No task updates these. This is intentional per design D5 — the approvalState field is kept as interim. The API continues to work unchanged.

3. **CheckSuiteRepo not deprecated**: The `check_suites` table and `CheckSuiteRepo` remain in code. No task marks them deprecated or removes them. T-009 creates the new table alongside. A future change should handle migration.

---

## Verdict

**PASS** — All artifacts are consistent, complete, and feasible after the 6 fixes applied. The dependency graph is valid with no cycles or forward references.
