# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx`
  Evidence: `IssueWorkflowProfileEditor` still renders inside the reading flow at lines 296-298, even though workflow-profile configuration is explicitly reference-rail content and excluded from the reading flow by `openspec/changes/issue-341/specs/issue-detail-reading-flow/spec.md:69-77` and `issue-detail-reference-rail/spec.md:5-8`. The candidate also adds a separate `WorkflowProfileControl` in the rail at lines 414-423, so workflow profile configuration is split across two tiers. [disallowed:product-behavior]
  SuggestedAction: Move or remove the profile YAML editor from the reading flow so workflow-profile configuration has one reference-rail home, or explicitly redefine the product/spec boundary if this editor is intended to remain primary content.
  Verification: Add a test asserting `reading-flow` does not contain `workflow-profile-editor-frame` and that all workflow profile configuration controls are in `reference-rail`; rerun `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/web/src/pages/issue-detail/ui/cards/IssueActionsCard.tsx`
  Evidence: `IssueActionsCard` is wrapped in the reference rail at `IssueDetailPage.tsx:453-487`, but the card still renders runtime decision signals: current task at `IssueActionsCard.tsx:75-79` and blocked reason at `IssueActionsCard.tsx:81-85`. Those duplicate the headline/runtime surface signal and violate the reference-rail spec that the rail holds metadata, low-frequency configuration, and non-runtime issue actions only. The current tests only check that runtime action buttons are absent from the rail, not that runtime status/task text is absent.
  SuggestedAction: Strip runtime current-task and blocked-reason snippets from `IssueActionsCard` when it is used as the non-runtime rail card, or split the non-runtime actions into a dedicated rail component.
  Verification: Add rail-exclusion tests for current-task and blocked-reason runtime text, plus the existing action-button exclusions; rerun the web typecheck and test suite.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx`
  Evidence: The reference rail renders `reference-rail-convergence` whenever `issue.health === IssueHealth.Blocked || issue.convergence` at lines 437-451. If an issue is blocked without a convergence payload, `WorkflowConvergencePanel` returns `null` (`packages/web/src/widgets/issue-workflow/ui/WorkflowConvergencePanel.tsx:7-8`), leaving a collapsed rail card whose only visible summary is the runtime status string `blocked`. That puts runtime blocked status into the reference rail and can expand to an empty body.
  SuggestedAction: Render the convergence rail card only when there is meaningful convergence content, or provide a real low-frequency convergence body; keep ordinary blocked runtime status in the status headline/surface.
  Verification: Add a blocked-without-convergence fixture asserting no empty convergence rail card is rendered and the blocked signal remains in the header tier.
  Status: open

- [ID: item-4]
  Severity: blocking
  Scope: interrupted/recovery path
  Evidence: The old standalone interrupted card was removed, but `deriveRuntimeDecision` does not classify `IssueHealth.Interrupted` as blocked. `IssueHealth.Interrupted` exists in `packages/web/src/entities/issue/model/issue.ts:35-40`, while `determineSummary` only checks `health === IssueHealth.Blocked`, `recovery.latestAttemptState === 'interrupted'`, or convergence at `packages/web/src/widgets/issue-workflow/model/derive-runtime-decision.ts:452-459`. The new cross-tier interrupted tests use `health: 'blocked'` with convergence (`IssueDetailPage.cross-tier.test.tsx:414-431`, `741-758`), so an actual interrupted-health projection without recovery/convergence can lose the promised interrupted signal after the card removal.
  SuggestedAction: Either teach the runtime decision reducer to treat `IssueHealth.Interrupted` as blocked/interrupted, or keep a collapsed rail/header signal for that projection shape.
  Verification: Add a regression fixture with `health: IssueHealth.Interrupted`, `workflowStatus: 'interrupted'`, and no convergence/recovery work item, asserting the headline is not running and the interrupted rationale is visible.
  Status: open

- [ID: item-5]
  Severity: warning
  Scope: sticky status headline placement
  Evidence: The headline is not the first content in the scroll container. `issue-detail-page-container` starts at `IssueDetailPage.tsx:149`, then renders the back button at lines 151-159 before `status-header-tier` begins at line 161. The status-header test describes first-child placement but only checks whether the scroll container's first child contains the headline as a descendant (`IssueDetailPage.status-header.test.tsx:195-199`), so it does not verify the task acceptance criterion that the headline is the first child/top status entry.
  SuggestedAction: Put the sticky headline before the back-navigation control, or update the spec/acceptance wording if the back link is intentionally allowed above it.
  Verification: Add an assertion on the actual first visible/top-level child order, not a descendant query, and manually verify scroll behavior.
  Status: open

- [ID: item-6]
  Severity: warning
  Scope: `packages/web/src/pages/issue-detail/ui/cards/CollapsibleRailCard.tsx`
  Evidence: Narrow-screen collapse is only seeded during initial mount: `useState(!(defaultCollapsed || forceCollapsed))` at line 22. `useNarrowViewport` listens for media-query changes (`packages/web/src/shared/lib/use-narrow-viewport.ts:17-21`), but existing expanded rail cards will remain expanded if the user resizes from desktop to narrow because `forceCollapsed` changes do not synchronize local state. This violates the narrow-screen requirement for stacked collapsed sections after responsive changes.
  SuggestedAction: Collapse cards when `forceCollapsed` transitions to true, or remount/key rail cards on rail-mode changes while preserving deliberate user expansion within a mode.
  Verification: Add a matchMedia change test that renders desktop-expanded cards, dispatches a narrow media change, and expects each rail card to become collapsed.
  Status: open

- [ID: item-7]
  Severity: minor
  Scope: reference rail visual hierarchy
  Evidence: Each rail item wraps an existing `CardSection` inside a new bordered `CollapsibleRailCard` (`CollapsibleRailCard.tsx:26-44` plus, for example, `IssueDetailsCard.tsx:17` and `CardSection.tsx:49-60`). Expanded rail sections therefore show duplicate titles and nested bordered card chrome, which works against the acceptance criterion that the reference rail is the lightest tier.
  SuggestedAction: Make rail card bodies render body-only content without an inner `CardSection` border/title, or let existing `CardSection` components provide the only card chrome and add collapse behavior around their header.
  Verification: Add snapshot/DOM assertions that expanded rail items do not render duplicate same-name headings and do not nest bordered card sections; confirm visually on desktop.
  Status: open

- [ID: item-8]
  Severity: test-gap
  Scope: `packages/web/src/pages/issue-detail/ui/*.test.tsx`
  Evidence: The new suites pass but miss several acceptance-critical negatives: `workflow-profile-editor-frame` remaining in `reading-flow`, runtime current-task/blocked-reason snippets inside the rail `IssueActionsCard`, actual `IssueHealth.Interrupted` projection handling, responsive collapse after viewport changes, and exact first-child/top placement of the sticky headline.
  SuggestedAction: Add focused regression tests for each negative placement/recovery case above before accepting the layout reorganization.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed with 263 files, 4114 tests passed, 1 skipped. These commands do not cover the gaps listed here.
  Status: open

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- [ID: item-9]
  Severity: info
  Scope: workflow artifacts
  Evidence: `openspec/changes/issue-341/` contains proposal, design, specs, tasks, progress, and self-review artifacts. Per the candidate boundary, these are expected workflow context/evidence and are not product deliverables by themselves.
  SuggestedAction: Leave workflow artifacts in place; only product/spec inconsistencies that affect implementation should block.
  Status: out-of-scope

<promise>FAIL</promise>
