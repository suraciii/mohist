## Why

The issue detail page has accumulated a batch of small, independently verifiable presentation defects that together erode trust in the page: Chinese copy leaks into the English UI, stateful blocks render with light-theme colors in dark theme, native selects break component consistency, a metadata label mislabels its row, a rail header truncates, session copy contradicts real usage, loading and error states conflate transient failure with a real 404, the reference rail scrolls out of view, and disabled buttons read as enabled. Each is a localized invariant violation; bundling them avoids nine near-identical issues while keeping every fix independently verifiable against its symptom.

## What Changes

- Replace the remaining Chinese strings on the issue detail page with English copy: the PR delivery indicator (`经由 PR #N 合并`) and the branch bar's upstream-unknown state (`未能检查上游`).
- Replace literal light-theme-tinted palette utilities (amber/blue/red/green/gray/purple/slate tints) on the branch bar and the per-task execution/progress log chrome with semantic theme tokens so the listed stateful blocks render correctly in dark theme. The execution log's deliberate dark console surface is preserved; other on-page literal palette is out of scope (see Impact).
- Replace the three native `<select>` filters in the sessions panel with the shared select component used elsewhere in the app.
- Correct the Details card label that duplicates "Parent Issue" for the child-issues row so each row's label describes its content.
- Stop truncating rail card headers so titles like "Configuration" render in full (or use a sensible abbreviation) at desktop rail width.
- Stop the sessions panel from claiming "No usage yet" when usage data exists; surface real usage or copy that states what is actually known.
- Replace the whole-page "Loading..." line with per-section skeletons, and distinguish a transient fetch error (offer retry) from a real not-found (404).
- Make the desktop reference rail stay visible while scrolling long pages.
- Make disabled buttons on the issue detail page (outside the decision surface, e.g. the empty-comment submit) visually unmistakable from enabled ones.

## Capabilities

- `issue-detail-copy`: Single-language (English) and truthful copy on the issue detail page — no Chinese strings leak, and no copy asserts a state the data contradicts (e.g. "No usage yet" when usage exists).
- `issue-detail-theme-tokens`: The branch bar and the per-task execution/progress log chrome use semantic theme tokens instead of literal light-theme-tinted palette utilities so the listed stateful blocks render correctly in dark theme; the execution log's deliberate dark console surface is preserved.
- `issue-detail-shared-selects`: Every select control on the issue detail page uses the shared select component rather than native `<select>` elements.
- `issue-detail-metadata-labels`: Details card row labels describe the data in their row; the parent reference and the child-issues rows are not both labeled "Parent Issue".
- `issue-detail-rail-presentation`: Rail card headers render untruncated at desktop width, and the desktop reference rail stays in view while the page scrolls.
- `issue-detail-async-states`: Loading renders per-section skeletons, and a transient fetch error offers retry and is visually distinct from the not-found (404) state.
- `issue-detail-disabled-affordance`: Disabled buttons on the issue detail page outside the decision surface are visually distinguishable from enabled ones at a glance.

## Impact

- **Web issue detail page** (`packages/web/src/pages/issue-detail`): the page-level loading/error branching (`IssueDetailPage.tsx`), the reference rail container stickiness, and the Details metadata labels (`cards/IssueDetailsCard.tsx`) change; the comment submit disabled affordance (`sections/IssueCommentsSection.tsx`) changes.
- **Web workflow widgets** (`packages/web/src/widgets/issue-workflow`): `BranchBar.tsx` (copy + theme tokens), `PrDeliveryIndicator.tsx` (copy), `WorkflowSessionsPanel.tsx` (native selects → shared component, "No usage yet" copy), and `TaskLogPanel.tsx` execution-log chrome (theme tokens; the dark console interior is preserved) change.
- **Theme-token scope reconciliation**: AC #2's "no literal palette utilities remain on the issue detail page; every listed block renders correctly in dark theme" is interpreted as scoping to the listed stateful blocks (branch bar + task execution/progress log). The stage-aware task list (`StageBar`/`StepList`/`TaskItem`) is already tokenized. The remaining on-page literal palette is intentionally out of scope: `WorkflowRunStatusPill` (not rendered on the page), `WorkflowSessionsPanel` status-icon colors and `PrDeliveryIndicator`/`ReviewSummary`/`ReviewReportModal`/`ArtifactContentViewer`/`ArtifactTextContent`/`CompositeParentOverview` containers and the composite-parent violet badge — these are either mid-saturation colors legible in dark theme or surfaces adjacent to the approval/artifact/composite concerns, and are tracked as follow-up rather than bundled here.
- **Web shared rail card** (`packages/web/src/pages/issue-detail/ui/cards/CollapsibleRailCard.tsx`): header truncation behavior changes.
- **Web shared primitives**: disabled affordance may touch the shared `Button` (`packages/web/src/shared/ui/components/button.tsx`) or per-button overrides; the shared `Select` (`packages/web/src/shared/ui/components/select.tsx`) is consumed, not changed. Loading skeletons may reuse or extend shared skeleton primitives.
- **Scope boundaries**: no server, runner, CLI, API, persistence, or routing contract changes; no behavior redesign or section restructuring (those belong to sibling reading-flow and decision-surface issues). Risk is rated low — scattered localized fixes, no contracts touched.
