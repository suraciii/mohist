## Why

The diff/commits viewer is buried at the bottom of IssueDetailPage below Comments and hidden behind a stage gate (`DIFF_STAGES`), forcing users to scroll through irrelevant content to answer their most pressing question: "what did the agent change?" This is especially painful during Plan approval, where the changes sit far from the approval panel. Repositioning changes to a first-class position — right after the description — eliminates this friction and makes the issue detail page's information hierarchy match the user's mental workflow.

## What Changes

- Remove `DIFF_STAGES` stage restriction so the Changes panel renders in all stages (Backlog shows empty state)
- Reposition the Changes panel from bottom-of-page (after Comments) to directly after Description, before TaskList
- Add summary statistics header to the Changes panel (N files changed, +X/-Y lines, M commits)
- Optionally surface a compact changes summary inside PlanApprovalPanel and ReviewApprovalPanel so reviewers see scope inline during approval

## Capabilities

### New Capabilities

- `changes-panel-prominence` — Changes panel as a first-class section in IssueDetailPage, visible in all workflow stages with summary statistics and empty-state handling

### Modified Capabilities

- `web-ui` — layout reordering in IssueDetailPage; removal of DIFF_STAGES gate for changes visibility

## Impact

- `packages/cli/web/src/components/IssueDetailPage.tsx` — layout reorder, remove DIFF_STAGES check
- `packages/cli/web/src/components/PlanApprovalPanel.tsx` — optional inline changes summary
- `packages/cli/web/src/components/ReviewApprovalPanel.tsx` — optional inline changes summary
- No API changes — reuses existing `getIssueDiff` and `getIssueCommits` endpoints
- No new dependencies
