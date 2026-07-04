## Why

The issue detail page lays every block — run progress, outputs, diff, commits, description, comments, drift, convergence, config, actions — into one undifferentiated vertical stack of same-weight cards. To answer "where is this issue right now?" the user must scroll through a dozen cards hunting for the one that matters. This change reorganizes the *same* information into three attention-graded layers — a sticky status headline, a reading flow, and a reference rail — so the current runtime situation is glanceable in one second and the work content follows in priority order. It is needed now because issue #340 already converged the single status adjudication and unified visual base that this layering depends on; without reorganizing the page around it, that adjudication still drowns in a flat card sea.

## What Changes

- Add a **sticky status headline** at the top of the detail page that aggregates, in one place, the current runtime situation (running / queued / approval-required / blocked / failed / done), the current stage with progress, and the current task title. It remains visible while the page scrolls and carries the heaviest visual weight. The decision/action surface (the single runtime control introduced by #340) anchors to this layer.
- Reshape the body into a **reading flow** (main column, max width, lightest container) ordered by user attention: workflow progress and outputs first, then changes/diff and commits, then the description, then comments.
- Introduce a **reference rail**: on desktop a right column, on narrow screens collapsed sections, holding only metadata and low-frequency configuration (details, model, workflow-profile control, prerequisites). Low-frequency items (drift, convergence) are collapsed by default and expand on demand.
- Keep the title/identity block carrying only identity (number, priority, draft/archived, title, labels, epic, timestamps) plus at most **one** runtime badge — no longer a flat row of same-weight runtime pills. This extends #340's badge grouping rule into the new layout.
- Establish a three-tier visual hierarchy: status headline (heaviest) > reading flow > reference rail (lightest).
- Within the reading flow, keep long blocks (e.g. description, change list) collapsible without losing their key signal when collapsed.

Out of scope (explicit): mobile-only affordances (bottom action bar / drawer), changing any block's underlying data source or query, introducing new blocks or content, and redoing #340's action-surface / color-base convergence.

## Capabilities

- `issue-detail-status-header`: The sticky top-of-page status headline — it aggregates the current runtime situation, current stage + progress, and current task title into a single glanceable region that stays in view while scrolling, anchors the decision/action surface, carries the heaviest visual weight, and governs the title/identity block showing identity plus at most one runtime badge.
- `issue-detail-reading-flow`: The main content column — it owns the attention-ordered sequence (workflow progress & outputs → changes/diff → commits → description → comments), the lightest-weight container at maximum width, the medium visual-weight tier, and the rule that collapsible long blocks preserve their key signal when collapsed.
- `issue-detail-reference-rail`: The reference column — it holds only metadata and low-frequency configuration (details, model, workflow-profile control, prerequisites), renders as a right rail on desktop and as collapsed sections on narrow screens, defaults low-frequency items (drift, convergence) to collapsed, and carries the lightest visual weight.

All three are **new** capabilities; the only living spec today is `issue-board`. No server, runner, or CLI capability is touched — the reorganization consumes data sources that already exist (`deriveRuntimeDecision`, `workflowStageProgress`, the workflow timeline, diff/commits queries).

## Impact

- **Web** (`packages/web`): `pages/issue-detail/ui/IssueDetailPage.tsx` (the top-level layout is restructured from a flat stack into the three layers), `pages/issue-detail/ui/pills.tsx` (`RuntimeSummaryPill` placement under the new header), and the existing block components are **repositioned, not rewritten**: `sections/IssueDescriptionSection`, `sections/IssueDiffFilesSection`, `sections/IssueCommitsSection`, `sections/IssueCommentsSection` move within the reading flow; `cards/IssueDetailsCard`, `cards/IssueConfigurationCard`, `cards/IssuePrerequisitesCard`, `cards/IssueDriftCard`, `cards/IssueReadinessCard`, plus `widgets/issue-workflow` (`WorkflowView`, `TaskProgressPanel`, `LatestArtifactsPanel`, `PrDeliverySummary`, `WorkflowConvergencePanel`, `WorkflowProfileControl`, `RuntimeDecisionSurface`) are reassigned to their target layer. A new sticky-header presentation component is introduced.
- **Server / runner / CLI**: none. No API, DTO, query, or data-source changes; the layering consumes the existing projections verbatim.
- **Dependencies**: none added.
- **Tests** (`packages/web`): the existing `IssueDetailPage.*.test.tsx` suite (archived, capacity-gating, readiness, main) asserts on `data-testid` anchors and layout order; these are updated to the new three-layer anchors and ordering. New spec tests cover stickiness, reading-flow ordering, reference-rail default-collapse, and the single-badge invariant.
- **Risk (medium)**: a layout reshuffle can regress the detail page's many conditional blocks (archived banner, backlog readiness, drift/convergence, interrupted health, PR delivery). Mitigated by reusing the existing block components unchanged and re-asserting every conditional path against the new layer assignment.
