# Review Report

## Result: PASS

Acceptance criteria evidence: `DashboardPage.tsx` renders the first screen as headline, attention, active production, capacity, and recent history in that order (`packages/web/src/pages/dashboard/ui/DashboardPage.tsx:118`). `DashboardPage.test.tsx` asserts attention -> pulse -> capacity -> digest ordering (`packages/web/src/pages/dashboard/ui/DashboardPage.test.tsx:298`), empty-zone collapse (`:357`), ready-state gating (`:448`), and capacity placement (`:656`). `deriveAttentionItems` now covers approval, integration-failed, interrupted, blocked, runner-unavailable, and runner-capacity-limited states (`packages/web/src/entities/issue/model/attention.ts:34`), with hero rendering for issue and runner rows (`packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx:91`). Running issues are sourced by `isRunningIssue` (`packages/web/src/entities/issue/model/running.ts:3`) and rendered in Pulse with workflow stage plus owner-action cue (`packages/web/src/widgets/dashboard-pulse/ui/PulseZone.tsx:40`, `packages/web/src/widgets/dashboard-pulse/ui/CompactSessionCard.tsx:146`). No backend contract or migration was changed; the existing `/agent/status` contract already returns `capacity.active` and `capacity.max` (`packages/server/src/Mohist.Server/Api/AgentRoutes.cs:144`).

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: formatting
  Evidence: `packages/web/src/widgets/dashboard-capacity/ui/DashboardCapacityZone.tsx` was missing a trailing newline. I added the newline only; the resulting diff for that file contains no behavior changes.
  Verification: `git diff --check` passed. `npm run typecheck -w packages/web` passed. Focused changed-file suite passed: `npm run test:run -w packages/web -- src/entities/issue/model/attention.test.ts src/entities/issue/model/running.test.ts src/pages/dashboard/ui/DashboardPage.test.tsx src/widgets/attention-hero/ui/AttentionHero.test.tsx src/widgets/coder-session/model/activity-cards.test.ts src/widgets/dashboard-capacity/ui/DashboardCapacityZone.test.tsx src/widgets/dashboard-pulse/ui/PulseZone.test.tsx src/widgets/kanban-board/ui/kanban-board-query.counts.test.tsx` (8 files, 176 tests). `npm run test:run -w packages/web -- src/widgets/factory-status/model/factory-status.test.ts` passed (1 file, 15 tests).
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

(none)

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: warning
  Scope: `packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx`
  Evidence: Full Web suite still fails outside the issue-399 dashboard diff. `npm run test:run -w packages/web` fails `TaskProgressPanel - task execution log panel > renders each line with source label, timestamp, and text` because Testing Library cannot find `08:00:00.000` at `TaskProgressPanel.test.tsx:272`. `git diff --name-only origin/master...HEAD -- packages/web/src/widgets/issue-workflow` returns no files, so this is outside the reviewed candidate deliverable.
  SuggestedAction: Fix the timestamp expectation or task-log rendering in a separate change, then rerun the full Web suite.
  Status: pre-existing

- [ID: item-3]
  Severity: info
  Scope: branch integration state
  Evidence: `git status -sb` reports `mohist/run-wr_6cbbd261f0e24e3bb0813223862734dd...origin/master [ahead 14, behind 7]`. This is outside the dashboard implementation but matters before integration because the candidate is not based on the latest `origin/master`.
  SuggestedAction: Rebase or merge upstream before integration if the workflow does not do that automatically, then rerun the affected Web checks.
  Status: out-of-scope

<promise>PASS</promise>
