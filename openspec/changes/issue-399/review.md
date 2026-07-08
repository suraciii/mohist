# Review Report

## Result: FAIL

## Repaired Items

(none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/pages/dashboard/ui/DashboardPage.tsx`
  Evidence: The concise ready state (`dashboard-ready-state`) can render while the runner status query is still loading. `DashboardPage` reads `const { data: agentStatus } = useAgentStatus()` (`DashboardPage.tsx:36`) and never consumes the hook's `isLoading` state. The `showReadyState` gate (`DashboardPage.tsx:71`) is `issuesResolved && activityResolved && !hasAttention && !hasActiveWork`. When `agentStatus` is `undefined` (loading), `hasAgentStatusActiveWork` (`DashboardPage.tsx:59-60`) evaluates to `false`, so `hasActiveWork` can be `false` even if the runner is about to report `running: true` or active agents. This lets the dashboard tell the owner "Nothing needs your attention right now" before it actually knows whether active work exists. The existing `DashboardPage.test.tsx` does not exercise this case: its `useAgentStatus` mock always returns a defined `data` object and never exposes `isLoading`. [disallowed:product-behavior-change]
  SuggestedAction: Consume `isLoading` from `useAgentStatus()` and extend `showReadyState` so it is `false` while the runner status is still loading. Add a regression test where issues and activity are resolved but `agentStatus` is loading/undefined; the ready state must not render until the runner status resolves.
  Verification: `npm run typecheck -w packages/web` passed. Targeted changed-file suite passed: `npm run test:run -w packages/web -- src/entities/issue/model/attention.test.ts src/entities/issue/model/running.test.ts src/pages/dashboard/ui/DashboardPage.test.tsx src/widgets/attention-hero/ui/AttentionHero.test.tsx src/widgets/coder-session/model/activity-cards.test.ts src/widgets/dashboard-capacity/ui/DashboardCapacityZone.test.tsx src/widgets/dashboard-pulse/ui/PulseZone.test.tsx src/widgets/factory-status/model/factory-status.test.ts src/widgets/kanban-board/ui/kanban-board-query.counts.test.tsx` (170 tests passed). The full web suite has one unrelated failure (see Pre-existing item).
  Status: unresolved

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx`, `packages/web/src/widgets/kanban-board/ui/KanbanBoard.tsx`
  Evidence: The union-narrowing guard `isIssueItem` (AttentionHero.tsx:25-29) and `isIssueAttentionItem` (KanbanBoard.tsx:452-456) are duplicated across two widgets. They both test the same two runner kinds to narrow to the issue branch of the `AttentionItem` union. The model already defines `IssueAttentionItem` as `Extract<AttentionItem, { issueId: string }>`, so the predicate belongs in the attention model.
  SuggestedAction: Export a single `isIssueAttentionItem(item): item is IssueAttentionItem` predicate from `entities/issue/model/attention.ts` and use it in both widgets. Re-export from `entities/issue/index.ts` if needed.
  Verification: `npm run typecheck -w packages/web` and the affected tests pass.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/web/src/widgets/dashboard-pulse/ui/CompactSessionCard.tsx`
  Evidence: The owner-action cue in the active-production zone uses `OWNER_ACTION_TREATMENT = statusTreatment('issue-health', 'blocked')` (CompactSessionCard.tsx:35), which is always danger family. In the attention hero the same issue kinds map to different families: `approval-needed` is warning, `interrupted` is warning, `integration-failed` is danger, and `blocked` is danger. The cue therefore distinguishes action-needed from normal running, but it loses the nuance that awaiting approval is a warning-level action.
  SuggestedAction: Route the owner-action cue through the same `attentionItemTreatment` mapping used for the attention hero rows so that the inline cue's family matches the corresponding attention entry.
  Verification: `npm run test:run -w packages/web -- src/widgets/dashboard-pulse/ui/PulseZone.test.tsx` and attention-equivalence specs.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/web/src/widgets/dashboard-capacity/ui/DashboardCapacityZone.tsx`, `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx`
  Evidence: The runner-capacity-limited attention item links to `/activity` (AttentionHero.tsx:299), while the capacity-level strip links to `/runners` (DashboardCapacityZone.tsx:93). Both are runner/capacity signals, so a user might expect them to land in the same place. The split is a design judgment, not a correctness bug, but the inconsistency should be intentional and recorded.
  SuggestedAction: Confirm the intended link target for each surface. If both should go to `/activity`, update the capacity-level link; if the capacity level should go to runner management, document the decision in the spec or progress notes.
  Verification: Add or update a test for `dashboard-zone-capacity-link` href.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/web/src/pages/dashboard/ui/DashboardPage.tsx`, `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx`
  Evidence: `DashboardPage` computes `attentionItems` once for the page-level gates, but `AttentionHero` recomputes its own items from its own `useIssues`/`useAgentStatus` hooks. The design states that `hasAttention` and `hasActiveWork` should be computed once from the shared hooks so the page and the hero cannot disagree. In practice they share the TanStack Query cache, so the recomputation is usually harmless, but it leaves the door open to inconsistent transient states.
  SuggestedAction: Consider passing `issues` and `agentStatus` from `DashboardPage` into `AttentionHero` (the component already accepts those props). This would require updating the `AttentionHero` mock path in `DashboardPage.test.tsx`.
  Verification: `npm run test:run -w packages/web -- src/pages/dashboard/ui/DashboardPage.test.tsx src/widgets/attention-hero/ui/AttentionHero.test.tsx`.
  Status: follow-up

- [ID: item-6]
  Severity: follow-up
  Scope: `packages/web/src/pages/dashboard/ui/DashboardPage.tsx`
  Evidence: `hasActiveWork` can be true solely because `agentStatus.running === true` or `agentStatus.activeAgents` is non-empty, even when `runningIssues` and `activeCards` are both empty. In that case the page renders the active-production zone, but `PulseZone` internally computes zero active rows and shows its own `pulse-empty-state` ("No active production"). The dashboard then presents an empty active-production box even though the page decided active work exists.
  SuggestedAction: Either render a brief placeholder for the active-agent signal inside the active-production zone, or tighten `hasActiveWork` so it only counts agent-status signals when the matching session data is expected to be present. This needs product/design input because the right UX depends on how `agentStatus.running`/`activeAgents` relate to the activity feed.
  Verification: Add a spec test where `agentStatus.activeAgents` is populated but `useAgentActivity` returns empty active cards; assert the zone renders meaningful content or is suppressed.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-7]
  Severity: warning
  Scope: `packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx`
  Evidence: The full web suite fails outside the issue-399 dashboard change. `npm run test:run -w packages/web` fails one test: `TaskProgressPanel — task execution log panel > renders each line with source label, timestamp, and text`, where Testing Library cannot find `08:00:00.000` at `TaskProgressPanel.test.tsx:272`. `git diff f7f1911e8..HEAD --name-only -- packages/web/src/widgets/issue-workflow/` returns no files, so this is outside the candidate deliverable.
  SuggestedAction: Fix or isolate the timestamp expectation separately; rerun the full web suite after that fix.
  Status: pre-existing

- [ID: item-8]
  Severity: info
  Scope: branch integration state
  Evidence: `git status -sb` reports `mohist/run-wr_6cbbd261f0e24e3bb0813223862734dd...origin/master [ahead 10, behind 3]`. This branch divergence is outside the reviewed dashboard implementation but matters before integration.
  SuggestedAction: Rebase or merge the upstream branch before integration if the workflow does not do that automatically, then rerun the affected checks.
  Status: out-of-scope

- [ID: item-9]
  Severity: info
  Scope: `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx`
  Evidence: `AttentionHero` retains its `AllClearState` and `LoadingState` branches. On the new dashboard these branches are never reached because `DashboardPage` only renders `<AttentionHero>` when `hasAttention` is true and renders the new `ReadyState` otherwise. The branches remain reachable from `AttentionHero.test.tsx` and any non-dashboard callers, so they are not dead code globally, but they no longer participate in the dashboard first-screen flow.
  SuggestedAction: If the dashboard is the only production caller, consider removing the all-clear/loading branches from `AttentionHero` and updating the cross-surface equivalence spec to compare the `ReadyState` instead. If other callers remain, keep the branches and document the split.
  Verification: Verify all callers of `AttentionHero` and update tests accordingly.
  Status: pre-existing

<promise>FAIL</promise>
