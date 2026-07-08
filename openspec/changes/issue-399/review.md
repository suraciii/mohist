# Review Report

## Result: FAIL

## Repaired Items

(none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/pages/dashboard/ui/DashboardPage.tsx`, `packages/web/src/widgets/coder-session/model/activity-cards.ts`
  Evidence: The dashboard can render the ready state while live work is still known or unresolved. `DashboardPage` computes active work only from `runningIssues.length > 0 || activeCards.length > 0` (`DashboardPage.tsx:49-64`), but it ignores `agentStatus.running` and `agentStatus.activeAgents` even though the same component already has `agentStatus` (`DashboardPage.tsx:36`) and uses it for the document title (`DashboardPage.tsx:42`). `useActivityCards()` collapses an unresolved `useAgentActivity()` query to empty `activeCards` and does not expose loading/error state (`activity-cards.ts:137-165`). Therefore, when issues have resolved to no running issue but the activity feed is still unresolved, `showReadyState` becomes true and the dashboard can say "Nothing needs your attention right now" even if `agentStatus.running === true` or `activeAgents` is non-empty. This violates the issue acceptance criteria that active production/current sessions are visible and that the concise ready state appears only when nothing is active. [disallowed:product-behavior-change]
  SuggestedAction: Include a reliable active-work signal while the activity feed is unresolved, either by exposing `isLoading`/`isError` from `useActivityCards()` and suppressing the ready state until the session source is resolved, or by folding `agentStatus.running`/`agentStatus.activeAgents.length` into the page-level active-work gate. Add a `DashboardPage.test.tsx` regression where issues are empty, the activity feed has not resolved, and `agentStatus.running` or `activeAgents` indicates live work; the ready state must not render.
  Verification: `npm run typecheck -w packages/web` passed. The targeted changed-file suite passed with 9 files and 181 tests: `npm run test:run -w packages/web -- src/entities/issue/model/attention.test.ts src/entities/issue/model/running.test.ts src/pages/dashboard/ui/DashboardPage.test.tsx src/widgets/attention-hero/ui/AttentionHero.test.tsx src/widgets/coder-session/model/activity-cards.test.ts src/widgets/dashboard-capacity/ui/DashboardCapacityZone.test.tsx src/widgets/dashboard-pulse/ui/PulseZone.test.tsx src/widgets/factory-status/model/factory-status.test.ts src/widgets/kanban-board/ui/kanban-board-query.counts.test.tsx`. The existing tests do not cover the unresolved-activity/agent-status-running case.
  Status: open

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: dashboard visual verification
  Evidence: The candidate has strong DOM/unit coverage for zone order, collapse, ready state, capacity, and active rows, but the design verification step called for a manual sweep of has-attention, active-only, idle/ready, and capacity-limited states in light and dark themes. No browser/screenshot evidence is present in the artifacts. This is not a blocker by itself because the relevant component tests pass, but it leaves the visual-prominence part of the spec verified structurally rather than visually.
  SuggestedAction: Before integration, run a browser sweep or screenshot check for the four dashboard states in both themes and confirm text wrapping, zone prominence, and first-screen density.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: warning
  Scope: `packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx`
  Evidence: The full web suite currently fails outside the issue-399 dashboard change. `npm run test:run -w packages/web` fails one test: `TaskProgressPanel — task execution log panel > renders each line with source label, timestamp, and text`, where Testing Library cannot find `08:00:00.000` at `TaskProgressPanel.test.tsx:272`. `git diff --name-only master...HEAD -- packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.tsx` returns no files, so this is outside the candidate deliverable.
  SuggestedAction: Fix or isolate the timestamp expectation separately; rerun the full web suite after that fix.
  Status: pre-existing

- [ID: item-4]
  Severity: info
  Scope: branch integration state
  Evidence: `git status -sb` reports `mohist/run-wr_6cbbd261f0e24e3bb0813223862734dd...origin/master [ahead 10, behind 3]`. This branch divergence is outside the reviewed dashboard implementation but matters before integration.
  SuggestedAction: Rebase or merge the upstream branch before integration if the workflow does not do that automatically, then rerun the affected checks.
  Status: out-of-scope

<promise>FAIL</promise>
