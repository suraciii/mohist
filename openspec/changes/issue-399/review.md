# Review Report

## Result: FAIL

## Repaired Items

(none)

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/pages/dashboard/ui/DashboardPage.tsx`
  Evidence: The ready-state gate does not account for the `agentStatus` query still loading. `DashboardPage` reads `const { data: agentStatus } = useAgentStatus()` (`DashboardPage.tsx:36`) and uses only `agentStatus` for attention/capacity/active-work checks, but it never consumes the hook's `isLoading` state. The `showReadyState` expression (`DashboardPage.tsx:70-71`) is `issuesResolved && activityResolved && !hasAttention && !hasActiveWork`. If `agentStatus` is still loading (undefined), `hasAgentStatusActiveWork` (`DashboardPage.tsx:59-60`) evaluates to `false` and `hasActiveWork` (`DashboardPage.tsx:61-62`) can become `false` even though the runner may later report `running: true` or active agents. Therefore the dashboard can render the concise ready state and tell the owner "Nothing needs your attention right now" while the runner status itself is still unresolved. This is the same correctness category as the previous unresolved-activity/active-work gap: the page should not claim "all clear" until it actually knows whether there is active work. [disallowed:product-behavior-change]
  SuggestedAction: Consume `isLoading` from `useAgentStatus()` and extend `showReadyState` so that it is `false` while the runner status is still loading. Add a `DashboardPage.test.tsx` regression where issues and activity are resolved but `agentStatus` is loading/undefined; the ready state must not render, and the page should remain in a non-ready state (not `idle` or `dashboard-ready-state`) until the runner status resolves.
  Verification: `npm run typecheck -w packages/web` passed. The targeted changed-file suite passed with 9 files and 185 tests: `npm run test:run -w packages/web -- src/entities/issue/model/attention.test.ts src/entities/issue/model/running.test.ts src/pages/dashboard/ui/DashboardPage.test.tsx src/widgets/attention-hero/ui/AttentionHero.test.tsx src/widgets/coder-session/model/activity-cards.test.ts src/widgets/dashboard-capacity/ui/DashboardCapacityZone.test.tsx src/widgets/dashboard-pulse/ui/PulseZone.test.tsx src/widgets/factory-status/model/factory-status.test.ts src/widgets/kanban-board/ui/kanban-board-query.counts.test.tsx`. The existing tests do not cover the agent-status-loading case.
  Status: unresolved

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx`, `packages/web/src/widgets/kanban-board/ui/KanbanBoard.tsx`
  Evidence: The type-narrowing guard `isIssueAttentionItem`/`isIssueItem` is defined twice in two different widgets (`AttentionHero.tsx:25-29`, `KanbanBoard.tsx:452-456`) rather than being exported once from the attention model. The `AttentionItem` union lives in `entities/issue/model/attention.ts`, and the model already defines the `IssueAttentionItem` extract type, so a shared predicate would keep the narrowing logic in one place.
  SuggestedAction: Export a single `isIssueAttentionItem(item): item is IssueAttentionItem` predicate from `entities/issue/model/attention.ts` and use it in both `AttentionHero` and `KanbanBoard`. Update `entities/issue/index.ts` to re-export it if needed.
  Verification: `npm run typecheck -w packages/web` and the affected tests pass.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/web/src/widgets/dashboard-pulse/ui/CompactSessionCard.tsx`
  Evidence: The owner-action cue is rendered with `OWNER_ACTION_TREATMENT = statusTreatment('issue-health', 'blocked')` (danger family) for every action kind: awaiting approval, integration-failed, interrupted, and blocked. In the attention hero, awaiting approval is mapped to the warning family, while the other three are danger/warning. A single danger cue is consistent but loses the nuance that an awaiting-approval issue is a warning-level action rather than a blocked-level one.
  SuggestedAction: Route the owner-action cue through the same `attentionItemTreatment` mapping used for the attention hero rows so that awaiting-approval rows render as warning and the failure states render as danger/warning. This keeps the inline cue visually aligned with the attention zone.
  Verification: `npm run test:run -w packages/web -- src/widgets/dashboard-pulse/ui/PulseZone.test.tsx` and attention-equivalence specs.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/web/src/widgets/dashboard-capacity/ui/DashboardCapacityZone.tsx`, `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx`
  Evidence: The runner-capacity-limited attention item links to `/activity` (runner status), while the capacity-level strip links to `/runners` (runner management). Both are capacity/runner signals, so a user might expect them to land in the same place. The current split is a design judgment, not a bug, but the inconsistency is worth recording.
  SuggestedAction: Confirm the intended link target for each surface. If both should go to `/activity`, update the capacity-level link; if the capacity level should go to runner management, document the decision in the spec/progress notes.
  Verification: Update or add a test for `dashboard-zone-capacity-link` href.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/web/src/pages/dashboard/ui/DashboardPage.tsx`, `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx`
  Evidence: `DashboardPage` computes `attentionItems` once for the page-level gate, but `AttentionHero` recomputes its own items from its own `useIssues`/`useAgentStatus` hooks. The design states that `hasAttention` and `hasActiveWork` should be computed once from the shared hooks so the page and the hero cannot disagree. In practice they share TanStack Query cache, so the recomputation is usually harmless, but it leaves the door open to inconsistent transient states.
  SuggestedAction: Consider passing `issues` and `agentStatus` from `DashboardPage` into `AttentionHero` (the component already accepts those props) so the page and the hero use exactly the same data snapshot. This would require updating the `AttentionHero` mock path in tests as well.
  Verification: `npm run test:run -w packages/web -- src/pages/dashboard/ui/DashboardPage.test.tsx src/widgets/attention-hero/ui/AttentionHero.test.tsx`.
  Status: follow-up

- [ID: item-6]
  Severity: follow-up
  Scope: `packages/web/src/pages/dashboard/ui/DashboardPage.tsx`
  Evidence: When `hasActiveWork` is true solely because `agentStatus.activeAgents` is non-empty (or `agentStatus.running` is true) but `runningIssues` and `activeCards` are both empty, the page renders the active-production zone (`DashboardZone id="pulse"`) and `PulseZone` internally computes zero active rows and shows its own `pulse-empty-state` ("No active production"). The dashboard then presents an empty active-production box, which contradicts the fact that the page decided active work exists.
  SuggestedAction: Either render a brief placeholder for the active-agent signal inside the active-production zone, or tighten `hasActiveWork` so that it only counts agent-status signals when the matching session data is expected to be present. This needs product/design input because the right UX depends on how `agentStatus.running`/`activeAgents` relate to the activity feed.
  Verification: Add a spec test where `agentStatus.activeAgents` is populated but `useAgentActivity` returns empty active cards; assert the zone renders meaningful content (or is suppressed).
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-7]
  Severity: warning
  Scope: `packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx`
  Evidence: The full web suite currently fails outside the issue-399 dashboard change. `npm run test:run -w packages/web` fails one test: `TaskProgressPanel — task execution log panel > renders each line with source label, timestamp, and text`, where Testing Library cannot find `08:00:00.000` at `TaskProgressPanel.test.tsx:272`. `git diff --name-only master...HEAD -- packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.tsx` returns no files, so this is outside the candidate deliverable.
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
  Evidence: `AttentionHero` retains its `AllClearState` and `LoadingState` branches. On the new dashboard these branches are never reached because `DashboardPage` only renders `<AttentionHero>` when `hasAttention` is true and renders the new `ReadyState` otherwise. The branches remain reachable from the cross-surface equivalence spec and `AttentionHero.test.tsx`, so they are not dead code globally, but they no longer participate in the dashboard first-screen flow.
  SuggestedAction: If the dashboard is the only production caller, consider removing the all-clear/loading branches from `AttentionHero` and updating the cross-surface equivalence spec to compare the `ReadyState` instead. If other callers remain, keep the branches and document the split.
  Verification: Verify all callers of `AttentionHero` and update tests accordingly.
  Status: pre-existing

<promise>FAIL</promise>
