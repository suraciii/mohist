## Why

The diff/commits viewer is buried at the bottom of IssueDetailPage (below Comments) and gated behind a `DIFF_STAGES` whitelist, making it invisible in Backlog and hard to find in all other stages. Users reviewing agent work must scroll past Description, TaskList, and Comments to answer their most pressing question: "what did the agent change?" — and during Plan/Review approvals, the file changes are spatially disconnected from the approval buttons in the sidebar.

## What Changes

- **Remove `DIFF_STAGES` stage restriction** — Changes panel visible in all workflow stages (Backlog shows empty state)
- **Reposition Changes panel** from bottom of main column (after Comments) to after Description, before TaskList
- **Add summary statistics** at the top of Changes panel: file count, +X/-Y lines, commit count
- **Add compact changes summary** inline in PlanApprovalPanel and ReviewApprovalPanel (optional, to connect approval context with actual scope)

## Capabilities

### New Capabilities

- `changes-panel-prominence`: Changes panel repositioned as a first-class section with summary stats, visible across all stages

### Modified Capabilities

- `changes-commits-first`: Remove `DIFF_STAGES` restriction; add summary statistics header; reposition from bottom to after Description

## Impact

- **Frontend** (`packages/cli/web/src/components/IssueDetailPage.tsx`): Remove `DIFF_STAGES` constant and `showDiff` guard; restructure JSX to move Changes section above TaskList
- **Frontend** (`packages/cli/web/src/components/PlanApprovalPanel.tsx`, `ReviewApprovalPanel.tsx`): Optionally add inline changes summary
- **No backend changes** — reuses existing `getIssueDiff` and `getIssueCommits` APIs
