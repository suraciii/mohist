# Review Self-Check

## Review Status: FAIL

## Checklist

- [x] Read all context files (proposal.md, design.md) before starting review
- [x] Read all source files changed by this issue
- [x] Ran `npm run build` — passed
- [x] Ran `npm test` — 1254 tests passed
- [x] Verified deleted components no longer exist on disk
- [x] Verified PipelineView is imported and rendered in IssueDetailPage
- [x] Verified SSE event registration in all three locations (events.ts, agent-events.ts, useSSE.tsx)
- [x] Checked type consistency between backend and frontend
- [x] Reviewed spec compliance for all 8 spec files

## Errors Found: 2

### E1: Build stage `stage_task_update` issueId mismatch
- **What**: `ralph-executor.ts:534` uses issue number string while Plan/Check use UUID
- **Impact**: SSE-driven real-time updates broken for Build stage; live elapsed timers don't work for Build tasks
- **Verified by**: Reading ralph-executor.ts (sseIssueId = String(issueNumber)), comparing with plan-stage-runner.ts (uses issue.id UUID), tracing frontend filter in PipelineView.tsx:694 and useSSE.tsx:232

### E2: CheckItem status value mismatch
- **What**: PipelineView.tsx:361 checks `'passed'` but backend stores `'pass'`; line 358 checks `'failed'` but backend stores `'fail'`
- **Impact**: All check items render as pending (empty circles) instead of showing pass/fail icons
- **Verified by**: Reading backend CheckResult in stage-context.ts:61 (`status: 'pass' | 'fail'`), tracing through StageExecutionRepo.persistCheckResults → API response → frontend CheckItem status comparisons

## Warnings Found: 3

- W1: Duplicated emitStageTaskUpdate helper across plan/check runners
- W2: Silent catch{} in BaseStageRunner.appendTaskResult
- W3: useIssueExecutions has no independent refetch (relies entirely on SSE invalidation)

## Acceptance Criteria Assessment

| # | Criterion | Met? |
|---|---|---|
| 1 | "Task" no longer ambiguous, each context has clear type | YES |
| 2 | No RoundConfig, unified Task type | YES |
| 3 | task_results stores StageTaskResult[], queryable per taskId | YES |
| 4 | All 3 stages emit stage_task_update, legacy preserved | PARTIAL (E1) |
| 5 | GET /api/issues/:number/executions returns structured data | YES |
| 6 | npm run build && npm test passes | YES |
| 7 | Stage Bar visible with current stage highlighted | YES |
| 8 | Step List shows Tasks + Checks | YES |
| 9 | Real-time updates via SSE | PARTIAL (E1 for Build) |
| 10 | Inline Approval in step list | YES |
| 11 | Old components removed | YES |
| 12 | Special states handled | YES |
| 13 | E2E full flow | NOT VERIFIED (requires running server) |
