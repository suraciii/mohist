## Why

The diff/commits viewer is buried at the bottom of IssueDetailPage, hidden behind a `DIFF_STAGES` gate that excludes Backlog entirely. Users must scroll past Description, TaskList, and Comments to see what the agent changed — and during approval reviews, the changes are spatially disconnected from the Approve button in the sidebar. Repositioning Changes to appear immediately after Description and removing the stage restriction makes code review a first-class interaction across every workflow stage.

## What Changes

- Remove `DIFF_STAGES` stage restriction — Changes panel visible in all stages (Backlog shows "No changes yet" empty state)
- Reposition Changes panel from bottom of main column (after Comments) to immediately after Description, before TaskList
- Add summary statistics header to Changes panel: file count, total additions/deletions, commit count
- Keep existing Files/Commits tabs and expandable DiffViewer behavior unchanged
- Optionally add compact changes summary inline in approval panels (PlanApprovalPanel, ReviewApprovalPanel) so reviewers see scope without scrolling

## Capabilities

### New Capabilities

- `changes-summary`: Summary statistics header for the Changes panel — displays file count, +/- line counts, and commit count at a glance

### Modified Capabilities

- `web-ui`: IssueDetailPage layout changes — Changes panel repositioned after Description (before TaskList), `DIFF_STAGES` restriction removed, visible in all stages with Backlog empty state
- `changes-tab`: Existing Changes panel gains a summary statistics header and all-stage visibility

## Impact

- **Frontend** (`packages/cli/web/src/components/IssueDetailPage.tsx`): Layout restructure — move diff/commits block up, remove `DIFF_STAGES` check, add summary stats
- **Frontend** (optional: `PlanApprovalPanel.tsx`, `ReviewApprovalPanel.tsx`): Compact inline changes summary during approval
- **No API changes** — reuses existing `getIssueDiff`, `getIssueCommits` endpoints
- **No diff rendering changes** — existing `DiffViewer` component untouched
