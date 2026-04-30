## Why

As issues accumulate in the Done column, the kanban becomes cluttered and hard to navigate. Users need a way to archive completed issues from the web UI — clearing the Done column while preserving access to archived history for reference or recovery.

## What Changes

- Done column filters out archived issues (API default behavior from #101)
- Done column footer shows archive summary: archived count + link to archive list + "Archive all completed" button
- Issue cards in Done column get an archive button (📦 icon)
- New `/archived` route: vertical list page for archived issues with search, restore, and navigation back to kanban
- API client (`api.ts`) adds `archiveIssue`, `unarchiveIssue`, `archiveAllCompleted`, `getArchivedIssues` methods
- Issue type gains `archivedAt?: string` field

## Capabilities

### New Capabilities

- `archive-kanban-ui` — Done column archive summary, per-card archive buttons, "Archive all completed" action
- `archive-list-page` — `/archived` route with searchable list of archived issues, per-item restore action

### Modified Capabilities

- `web-ui` — new `/archived` route added to router; API client methods for archive endpoints

## Impact

- **Frontend only**: `packages/cli/web/` — new components, new route, api.ts additions
- **Depends on**: Issue #101 backend API endpoints (POST `/api/issues/:number/archive`, POST `/api/issues/:number/unarchive`, GET `/api/issues?archived=true`, POST `/api/issues/archive-completed`)
- **No backend changes**: all archive API endpoints are provided by #101
