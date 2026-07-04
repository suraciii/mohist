# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs; packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx
  Evidence: A running task that has already persisted more than `TASK_LOG_RETAINED_LIMIT` lines can show a stale head page when the user expands it mid-run. The runner keeps only the retained tail in memory (`MAX_TASK_LOG_LINES = 5000` at `packages/runner/src/runtime/task-log.ts:36`, head-drop at `packages/runner/src/runtime/task-log.ts:274`), but the server keeps every successful incremental row until the terminal batch prunes stale rows (`terminal` pruning only starts at `packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs:58`). While the task is still running, `QueryAsync` returns the oldest page after `cursor` in ascending seq order (`packages/server/src/Mohist.Server/Infrastructure/Data/Runner/TaskLogStore.cs:149`, `:157`, `:168`) and the panel fetches only `limit: 5000` once (`packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:98`). The panel never follows `nextCursor`; it only merges future `OnTaskLogDelta` entries into that first page (`packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:66`, `:83`, `:120`). Concrete failure: if seq 1..6000 have already been incrementally stored and the user opens the panel before terminal reconciliation, the initial query returns seq 1..5000 with `nextCursor=5000`; the retained live tail should be seq 1001..6000. Even after a future delta seq 6001, `mergeTaskLogDelta` trims only one old row and displays seq 2..5000 plus 6001, still missing the current tail seq 5001..6000. This violates the issue's running-view acceptance criterion: the expanded task should show execution progress in near-real time, especially where the long task is currently stuck. [disallowed:product-behavior-change]
  SuggestedAction: Make the running-state bootstrap fetch converge to the current retained tail, not the oldest persisted page. Options include adding a tail query mode for task logs, paging to the end before displaying a running task, or having the server maintain/prune a running retained-tail view consistent with the runner cap. Add a regression test for opening a running task after more than 5000 incremental rows are already persisted, asserting the displayed lines are the retained tail and future deltas append from there.
  Verification: `npm test`, `npm test -w packages/runner`, `npm run typecheck -w packages/runner`, `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`, and `git diff --check master...HEAD` all passed. Existing tests cover terminal pruning (`TaskLogStoreSpecs.AppendAsync_TerminalBatchPrunesRowsOutsideRetainedTail`) and live-cache trimming (`TaskLogPanel.test.tsx` `keeps only the retained tail...`), but not late expansion while the task is still running and the store has more than one page of incremental rows.
  Status: open

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx; packages/web/src/shared/api/events-hub.ts
  Evidence: Each rendered `TaskLogPanel` opens its own SignalR connection through `useEventsConnection` (`packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:123`), even for terminal tasks where it will never subscribe to live deltas. `applyDefaultSubscriptions: false` prevents unrelated domain/transcript fan-out, so this is not a correctness failure, but it can create unnecessary connections when users expand several completed logs.
  SuggestedAction: Consider sharing the app-level events connection or delaying task-log-only connection creation until the task is running and has a valid `workflowRunId`.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: info
  Scope: openspec/changes/issue-337/
  Evidence: Workflow artifacts under `openspec/changes/issue-337/` are review context for this Mohist workflow. Their presence is expected during Plan/Build/Check/Integrate and is not treated as a product-deliverable failure.
  SuggestedAction: None.
  Status: out-of-scope

<promise>FAIL</promise>
