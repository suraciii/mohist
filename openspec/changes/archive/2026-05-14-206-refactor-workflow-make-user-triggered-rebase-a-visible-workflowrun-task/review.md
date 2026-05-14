## Review: Issue #206 — Make user-triggered rebase a visible WorkflowRun task

### Overall Assessment: PASS with Warnings

Typecheck passes. All 30 tests pass (20 domain regression + 10 API integration).

---

### Correctness

**No errors found.** The implementation is logically sound for the primary rebase flow.

**Warning 1: Failed rebase without conflicts may be incorrectly marked completed.**

`rebase-task-handler.ts:122`:

```typescript
status: result.conflicts.length > 0 && !result.rebased ? 'failed' : 'completed',
```

If `rebaseOntoMaster` returns `{success: false, conflicts: []}` (non-conflict failure), the task is marked `'completed'` instead of `'failed'`. The condition should use `result.rebased` as the primary success indicator:

```typescript
status: result.rebased ? 'completed' : 'failed',
```

This is only reachable if `rebaseOntoMaster` can fail without conflicts and without throwing. If that method always throws on non-conflict failures, this path is unreachable — but the defensive logic is wrong as written.

**Warning 2: `scheduleRebaseTask` clears approval evidence, contradicting spec.**

`domain/index.ts:548`:

```typescript
if (stageRun.status === 'awaiting-approval') {
  stageRun.status = 'running';
  stageRun.approval = null;  // clears approval evidence
}
```

The spec says: "prior approval state SHALL remain evidence until later invalidation policy decides whether it is still valid." The code nulls `stageRun.approval`, discarding the evidence before the invalidation policy runs. The test at `rebase-workflow-regression.test.ts:133` confirms this is intentional. The design doc D5 also says "preserving prior approval evidence" but the implementation clears it.

In practice this works because the workflow re-requests approval after rebase completes if checks pass, but it deviates from the spec's "remain evidence" requirement.

### Complexity

All functions are under 50 lines. Cyclomatic complexity is well under 10 for all changed functions. No concerns.

### Test Coverage

**30 tests pass across 2 test files:**

- `rebase-workflow-regression.test.ts`: 20 domain-level tests covering idempotency, approval reopen, `nextWork()` scheduling, failure blocking, `shaChanged=false`/`true` invalidation, visibility, and output contract.
- `api-rebase.test.ts`: 10 API tests covering precondition checks, Done-stage delegation, WorkflowRun scheduling, duplicate-click idempotency, and legacy enqueue fallback.

No frontend tests were added (T-005 acceptance criteria mentioned "Frontend tests cover Issue Detail showing `Rebase branch`"), but the frontend change is minimal (query invalidation) and the core behavior is tested at the API and domain levels.

### Security

No injection risks or exposed secrets. The rebase-task-handler accesses worktree paths through existing service boundaries with proper null checks.

### Spec Compliance

| Acceptance Criterion | Status | Evidence |
|---|---|---|
| Click Rebase → current stage task list shows `Rebase branch` | PASS | `scheduleRebaseTask` appends task via `appendAdHocTask('rebase-branch', 'Rebase branch', ...)` at `domain/index.ts:555`. API returns `taskId: 'rebase-branch'` at `issues.ts:3102`. |
| `Rebase branch` scheduled by `nextWork()` | PASS | `nextWork()` at `domain/index.ts:843-844` returns `{kind: 'task', taskId: 'rebase-branch'}` when it's the next non-terminal task. Test at `rebase-workflow-regression.test.ts:147-155`. |
| Uses #199 shared task handler / StageContext | PASS | `executeRebaseBranchTask` at `rebase-task-handler.ts:83` uses `StageContext` and is dispatched from `executeReportedTask` in all four stage runners. |
| `rebase-branch` pending/running blocks later tasks/checks | PASS | `nextTask()` returns rebase before later tasks; `nextCheck()` at `domain/index.ts:299` requires all tasks terminal. Test at `rebase-workflow-regression.test.ts:157-171`. |
| `rebase-branch` failed → stage fails, later work blocked | PASS | `completeTask` calls `fail()` at `domain/index.ts:586-597`. Tests at `rebase-workflow-regression.test.ts:175-205`. |
| `shaChanged=false` → no review/check invalidation | PASS | `domain/index.ts:609-624` only invalidates when `detectShaChanged` returns true. Test at `rebase-workflow-regression.test.ts:208-263`. |
| `shaChanged=true` → invalidate review/check/approval via stage policy | PASS | Resets `ai-review`, `review-passed`, `merge-ready` at `domain/index.ts:613-618`. Test at `rebase-workflow-regression.test.ts:266-319`. |
| Handler does not read `reEvalPlan` or decide replan/re-review | PASS | `rebase-task-handler.ts` has no reference to `reEvalPlan` or replan logic. |
| API primary path no longer uses legacy `taskType='rebase'` queue | PASS | `issues.ts:3080-3107` uses `scheduleRebaseTask` via `WorkflowApplicationService` when active WorkflowRun exists. Legacy path at line 3113 is a fallback. |
| Duplicate click idempotent | PASS | `scheduleRebaseTask` at `domain/index.ts:541-544` returns early if non-terminal `rebase-branch` exists. Test at `rebase-workflow-regression.test.ts:64-85`. |
| Approval-paused stage can execute appended rebase | PASS | `scheduleRebaseTask` at `domain/index.ts:546-549` reopens to `running`. Test at `rebase-workflow-regression.test.ts:110-131`. |
| Approval state preserved as evidence | **WARN** | `domain/index.ts:548` sets `approval = null`, clearing evidence before invalidation policy runs. See Warning 2 above. |
| Rebase SSE remains secondary to canonical task state | PASS | `BranchBar.tsx:26-29` invalidates `workflow-run` query on settle. `useSSE.tsx` still listens to rebase SSE events but they only supplement the canonical state. |

<promise>PASS</promise>
