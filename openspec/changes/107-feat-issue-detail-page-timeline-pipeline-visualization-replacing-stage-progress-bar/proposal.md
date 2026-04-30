## Why

Users returning to the Issue Detail page after stepping away have no unified view of what happened during their absence. The current page only provides a state snapshot (stage, status, tasks, comments), missing the narrative from creation to now. The horizontal progress bar tells "where" but not "how we got here" — users must mentally piece together SessionList + TaskList + approval_state + workflow_log to understand the full story. The pipeline metaphor (Plan → Build → Review → Done) is mohist's core, yet the UI fails to visualize it as such.

## What Changes

- Replace the horizontal stage progress bar in IssueDetailPage with a vertical pipeline timeline
- Create `IssueTimeline.tsx` component with three-level collapsible information
- Create `useIssueTimeline(issueNumber)` hook aggregating existing APIs (useIssue, useCoderSessions, workflow_log)
- Timeline nodes: Created → Plan → Approved → Build → Review → Done (each showing status + duration)
- Current stage highlighted (running/awaiting approval); future stages shown as pending (gray)
- Expand completed stages to reveal internal details (Plan rounds, Build tasks)
- Expanded view includes "View session" link to SessionPage
- Real-time updates via SSE events (plan_round_start/complete, ralph_task_update, build_started/completed)
- Responsive layout for mobile

## Capabilities

### New Capabilities

- `issue-timeline-ui`: Vertical pipeline timeline replacing the horizontal stage progress bar on IssueDetailPage, showing the complete narrative from issue creation to current state with three-level collapsible details

### Modified Capabilities

- `session-timeline-ui`: The existing "Pipeline status timeline" requirement (session-timeline-ui/spec.md lines 48-53) overlaps with this change. The requirement there specifies the same pipeline visualization behavior. This change provides the implementation details and replaces the existing stage progress bar as the source of truth for pipeline visualization.

## Impact

- `packages/cli/web/src/components/IssueDetailPage.tsx` — replace horizontal progress bar with IssueTimeline component
- `packages/cli/web/src/components/IssueTimeline.tsx` — new timeline component
- `packages/cli/web/src/hooks/useIssueTimeline.ts` — new hook aggregating issue + sessions + workflow_log data
- Reuses: `SessionTimeline.tsx` round inference logic, `useCoderSessions.ts` for session list
- No backend API changes; all data inferred client-side from existing endpoints
