## Why

The diff/commits viewer is buried at the bottom of IssueDetailPage below comments, gated behind a `DIFF_STAGES` set that excludes Backlog, and invisible during early stages — forcing users to scroll past irrelevant content to answer the most basic question: "what did the agent change?" This makes approval review friction high and hides scope information when it matters most.

## What Changes

- Remove `DIFF_STAGES` stage restriction — show the Changes panel in all workflow stages
- Reposition the Changes section from after Comments to after Description (before TaskList)
- Add a summary header to the Changes panel (file count, +/- lines, commit count)
- Show an empty state ("No changes yet") in stages with no changes (e.g., Backlog)
- Optionally add a compact changes summary inline in the approval gate sections of the sidebar

## Capabilities

### New Capabilities

- `changes-panel-prominence` — Changes panel always visible, prominently positioned with summary stats, across all stages

### Modified Capabilities

- `web-ui` — IssueDetailPage layout changes: new position for Changes section, removed DIFF_STAGES gate

## Impact

- `packages/cli/web/src/components/IssueDetailPage.tsx` — remove `DIFF_STAGES` constant and `showDiff` guard, reorder JSX to move Changes above TaskList/Comments, add summary header, add inline changes summary in approval gate sections (PlanApprovalPanel/ReviewApprovalPanel exist but are not currently used in this page)
- No API changes — reuses existing `getIssueDiff` and `getIssueCommits` endpoints
