# Review Report

## Result: FAIL

## Repaired Items

(none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/pages/dashboard/ui/DashboardPage.tsx`
  Evidence: The concise ready state can render after the issue list or runner status query fails with no cached data. `DashboardPage` reads only `isLoading` from `useAgentStatus()` and `useIssues()` (`DashboardPage.tsx:36-37`), then treats `fetchedIssues === undefined && issuesLoading === false` as resolved (`DashboardPage.tsx:67`) and `agentStatus === undefined && agentStatusLoading === false` as resolved (`DashboardPage.tsx:69`). Because `isError` is ignored, a failed issue or runner-status query can make `hasAttention` and `hasActiveWork` both false (`DashboardPage.tsx:48-63`) and still satisfy `showReadyState` (`DashboardPage.tsx:72`). That violates the ready-state acceptance criterion: the dashboard can say "Nothing needs your attention right now" before it actually knows whether approval gates, blocked/interrupted issues, runner unavailability, or active work exist. The regression tests cover loading gates (`DashboardPage.test.tsx:447-492`) but not error/no-data gates. [disallowed:product-behavior-change]
  SuggestedAction: Consume `isError` from both `useIssues()` and `useAgentStatus()`. Do not render the ready state when either query has failed without usable data; render an explicit compact error/unavailable state or keep the dashboard out of all-clear until the data is known. Add regression tests for issue-query error and runner-status-query error with `data: undefined`, asserting `dashboard-ready-state` is absent.
  Verification: `npm run typecheck -w packages/web` passed. Changed-file suite passed: `npm run test:run -w packages/web -- src/entities/issue/model/attention.test.ts src/entities/issue/model/running.test.ts src/pages/dashboard/ui/DashboardPage.test.tsx src/widgets/attention-hero/ui/AttentionHero.test.tsx src/widgets/coder-session/model/activity-cards.test.ts src/widgets/dashboard-capacity/ui/DashboardCapacityZone.test.tsx src/widgets/dashboard-pulse/ui/PulseZone.test.tsx src/widgets/factory-status/model/factory-status.test.ts src/widgets/kanban-board/ui/kanban-board-query.counts.test.tsx` (9 files, 189 tests). Full `npm run test:run -w packages/web` fails on an out-of-scope test listed below.
  Status: unresolved

## Follow-up Items

(none)

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: warning
  Scope: `packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx`
  Evidence: The full Web suite fails outside the issue-399 dashboard diff. `npm run test:run -w packages/web` fails `TaskProgressPanel — task execution log panel > renders each line with source label, timestamp, and text` because Testing Library cannot find `08:00:00.000` at `TaskProgressPanel.test.tsx:272`. `git diff --name-only origin/master...HEAD -- packages/web/src/widgets/issue-workflow` returns no files, so this failure is outside the reviewed candidate deliverable.
  SuggestedAction: Fix the timestamp expectation or task-log rendering in a separate change, then rerun the full Web suite.
  Status: pre-existing

- [ID: item-3]
  Severity: info
  Scope: branch integration state
  Evidence: `git status -sb` reports `mohist/run-wr_6cbbd261f0e24e3bb0813223862734dd...origin/master [ahead 13, behind 7]`. This is outside the dashboard implementation but matters before integration because the candidate is not based on the latest `origin/master`.
  SuggestedAction: Rebase or merge upstream before integration if the workflow does not do that automatically, then rerun the affected Web checks.
  Status: out-of-scope

<promise>FAIL</promise>
