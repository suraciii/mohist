## Why

The web `IssueDetailPage` is a god-component (scc Complexity 211 / 1375 lines) holding 13 lifecycle/comment/prerequisite mutations, 9 inline sub-components, a right-rail Actions card that is a ~300-line state machine over `isArchived × isBacklog × isAgentRunningOnThis × recovery.allowedActions × workflowTimeline.availableActions`, and two near-duplicate click-outside + 5s auto-dismiss effects (force-stop and stop). Any UI adjustment means threading through unrelated concerns in one file. This is the next hotspot under the "代码复杂度热点治理" epic blocking safe iteration on the issue detail surface.

## What Changes

- Promote the 9 inline presentational sub-components (`PriorityChip`, `WorkflowStagePill`, `HealthPill`, `DraftPill`, `ArchivedPill`, `WorkflowYamlDialog`, and the header/label helpers) into their own files under `packages/web/src/pages/issue-detail/`.
- Split the main column into `IssueDescriptionSection`, `IssueDiffFilesSection`, `IssueCommitsSection`, and `IssueCommentsSection`; split the right rail into `IssueDetailsCard`, `IssueDriftCard`, `IssueConfigurationCard`, `IssueActionsCard`, `IssuePrerequisitesCard`, and `IssueReadinessCard`.
- Merge the two duplicated click-outside + 5s auto-dismiss effects into a single reusable `useConfirmOutsideClick()` hook; migrate `forceStopPanelRef` / `stopPanelRef` alongside the Actions card so the shared refs stay co-located with their consumers.
- Extract the 13 mutations into a `useIssueDetailMutations()` hook (or co-locate them with their owning sub-component), so the page body converges to orchestration only.
- No **BREAKING** changes: every `data-testid`, the rendered DOM/margins, TanStack Query keys (`['issues']` / `['issues', issueNumber]` / `['agent-status']`), navigation URLs (Ask Agent, View files, edit, epic, back-to-board/back-to-archived), and the issue-300 capacity-gating contract (`isCapacityFull` derivation from `agentStatus.capacity`) are preserved bit-for-bit.

## Capabilities

### New Capabilities

_None._ This change introduces no new user-visible or system behavior; it is a pure internal restructuring.

### Modified Capabilities

_None._ Existing issue-detail behavior — visual presentation, workflow/action state-machine transitions, capacity gating, comment/prerequisite mutations, and navigation — is preserved bit-for-bit. The `web-ui` spec describes behavior (model overrides, workflow-profile consistency, archived-detail history rendering), not implementation layout, and no spec-level requirement changes. All acceptance is structural (file placement, complexity, unchanged render output) rather than behavioral.

## Impact

- **Code** (`packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx`):
  - `IssueDetailPage.tsx` — slimmed to page-level orchestration (data loading, layout slots, dialog mount); loses inline sub-components, the Actions state machine, and the duplicated click-outside effects.
  - New files under `packages/web/src/pages/issue-detail/`: presentational chips/pills, the per-section components named above, `useConfirmOutsideClick()` hook, and `useIssueDetailMutations()` hook.
  - Public route entry (`IssueDetailPage` export) unchanged; external consumers require zero changes.
- **Tests**: the existing 4 component suites (`IssueDetailPage.test.tsx`, `.archived.test.tsx`, `.capacity-gating.test.tsx`, `.readiness.test.tsx`, ~71 tests including `data-testid` + density regression assertions) are the regression guard and must pass unchanged. No new behavior tests are added; extracted hooks may gain direct unit tests where they raise testability.
- **APIs / Dependencies / Systems**: none. No server, runner, or CLI changes; no HTTP API, Query-key, or routing change; no new dependencies.
- **Risk**: low — pure frontend, behavior-invariant, thickly guarded by existing component tests.
