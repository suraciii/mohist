# Self-Review Report

## Verdict: PASS

## Completeness: PASS
- All 4 issue requirements (plan/build/review/done rebase) covered by specs
- All 4 UI requirements (button visibility, API call, rename, SSE progress) covered by specs
- All 5 spec files have corresponding tasks in tasks.json
- Edge cases covered: worktree missing, agent running, unsupported stage, already up to date, conflict abort, build verify failure

## Consistency: PASS
- Proposal Capabilities section matches spec files (2 new + 3 modified)
- Task spec references all point to correct spec files
- Event names consistent across event-bus spec, issue-rebase-api spec, issue-rebase-ui spec, and tasks
- Design decisions (D1–D6) align with task implementation approach
- Naming consistent: "Rebase onto master" (button), "Rebase and Retry" (rename), rebase_started/progress/completed/conflict (events)

## Feasibility: PASS
- `worktreeManager.canFastForward()` and `rebaseOntoMaster()` exist in codebase (change #73)
- `agentRunner.isRunning()` pattern used in 4 existing routes — same pattern for T-002
- `checkpointRepo` already injected into `createIssueRoutes` for T-004
- `agentRunner` and `agentSessionMessageRepo` already injected for T-005
- Build verify pattern follows existing `MergeQueue.processItem()` for T-003
- Frontend follows existing patterns: `api.retryMerge()` for API client, `eventTypes` array in `useSSE` for SSE subscription

## Dependency Completeness: PASS
- T-001 (priority 1): `dependsOn: []` — valid, first task
- T-002 (priority 2): `dependsOn: ["T-001"]` — needs event types to emit ✓
- T-003 (priority 3): `dependsOn: ["T-002"]` — extends route handler ✓
- T-004 (priority 4): `dependsOn: ["T-002"]` — extends route handler ✓
- T-005 (priority 5): `dependsOn: ["T-002"]` — extends route handler ✓
- T-006 (priority 6): `dependsOn: ["T-001"]` — needs event types to subscribe ✓
- T-007 (priority 7): `dependsOn: []` — pure standalone UI label rename, no backend dependency
- T-008 (priority 8): `dependsOn: ["T-006", "T-007"]` — needs API client + naming context ✓
- No cycles in dependency graph ✓
- All references point to existing task IDs with lower priority ✓

## Quality: PASS
- All specs use SHALL/MUST language ✓
- All scenarios use `#### Scenario:` heading format ✓
- All tasks have verifiable acceptance criteria ✓
- All tasks include mode, type, output, dependsOn fields ✓

## Fixes Applied
1. **Fixed `draft` → `backlog`/`explore` in specs**: Backend Stage enum has `backlog`, `explore`, `plan`, `build`, `review`, `done` — no `draft` stage exists. Updated `issue-rebase-api/spec.md` rejection scenario, `issue-rebase-ui/spec.md` button visibility scenarios, and corresponding task acceptance criteria in `tasks.json`.
2. **Fixed proposal Impact section**: Removed misleading `server/index.ts` and `types/index.ts` entries (route auto-registers via `createIssueRoutes`, event types go in `event-bus.ts` and `events.ts`). Added correct file paths for event-bus.ts, events.ts, api.ts, and useSSE.ts.
