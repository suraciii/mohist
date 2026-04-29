## Why

When a user manually closes an issue, `IssueService.close()` sets `status=Closed` but leaves `stage` unchanged. The closed issue remains stuck in its original Kanban column (Plan, Build, etc.), polluting the active workspace and defeating the user's intent to stop caring about that issue. Additionally, `IssueCard` renders `Blocked` status with a gray "Closed" overlay — visually conflating two distinct states and making Blocked issues harder to notice.

## What Changes

- KanbanBoard grouping logic redirects `status=Closed` issues into the Done column (display-layer only, backend `stage` unchanged)
- KanbanView/App gains a "Show closed" toggle (default off) controlling Done column visibility for closed issues
- IssueCard replaces the gray overlay for `Blocked` with a distinct red/orange "Blocked" badge; adds a gray "Closed" badge for truly closed issues without overlay
- Reopen naturally restores issue to its original stage column (no backend stage mutation needed)

## Capabilities

### New Capabilities

- `kanban-closed-filtering` — toggle to show/hide closed issues in the Done column, defaulting to hidden

### Modified Capabilities

- `web-ui` — KanbanBoard column grouping rules expand to redirect closed issues; IssueCard badge rendering separates Blocked and Closed with distinct visual treatments

## Impact

- `packages/cli/web/src/components/KanbanBoard.tsx` — column grouping logic
- `packages/cli/web/src/components/IssueCard.tsx` — badge/overlay rendering
- `packages/cli/web/src/components/KanbanView.tsx` (or `App.tsx`) — "Show closed" toggle
- Pure frontend change; no backend, API, or database changes
