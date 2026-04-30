## Context

This is a frontend-only change. The kanban board (`KanbanBoard`, `StageColumn`, `IssueCard`) currently displays all issues grouped by stage. The Done column has no concept of archiving — all completed issues remain visible. Backend archive APIs from Issue #101 already exist.

Current data flow: `KanbanView` fetches issues via `useIssues({ projectId })` → `KanbanBoard` groups by stage → `StageColumn` renders cards → `IssueCard` displays each issue. All mutations use `useMutation` with `queryClient.invalidateQueries({ queryKey: ['issues'] })` for refresh.

## Goals / Non-Goals

**Goals:**
- Add archive/unarchive controls to the kanban Done column and issue cards
- Provide a dedicated `/archived` page for browsing and restoring archived issues
- Wire up frontend API client to backend archive endpoints

**Non-Goals:**
- Auto-archive rules or scheduled archiving
- Archive statistics or analytics
- Backend changes (all provided by #101)

## Decisions

### D1: Fetch archived count alongside kanban issues

The Done column footer needs the count of archived issues. Rather than a separate API call, add a `useArchivedIssues` query (via `GET /api/issues?archived=true`) that the Done column's `StageColumn` can consume for the count. The archived page will reuse the same query with its own cache key.

**Alternatives considered:**
- Separate `/api/issues/archived-count` endpoint — requires backend change, rejected since #101 already provides `?archived=true`.
- Embed count in status API — overloads an existing endpoint for a UI-specific need.

### D2: Archive button lives inside IssueCard via a new prop

Add a `showArchiveButton` boolean prop to `IssueCard`. When true and the issue is `completed`, render the archive button. `StageColumn` passes `showArchiveButton={isDone}`. The button uses `useMutation` calling `api.archiveIssue`, invalidating `['issues']` and `['archived-issues']` on success.

**Alternatives considered:**
- Separate wrapper component — adds indirection for a single button.
- Context-based approach — over-engineered for a single boolean signal.

### D3: Archive summary footer rendered inside StageColumn

`StageColumn` already has `isDone` prop. When `isDone` is true, render a footer section below the issue cards. The footer receives `archivedCount` as a new prop (derived from the archived issues query in `KanbanBoard`). This keeps StageColumn self-contained.

**Alternatives considered:**
- Render footer in `KanbanBoard` outside `StageColumn` — breaks the column layout.
- Separate `DoneColumnFooter` component rendered by `KanbanBoard` — possible but requires layout coordination; simpler to keep in `StageColumn`.

### D4: ArchivedPage as a standalone page component

Create `ArchivedPage.tsx` in `packages/cli/web/src/components/`. It uses `useArchivedIssues()` hook to fetch data, manages local search state with `useState`, and renders a vertical list. Each item is a card linking to `/issue/:number` with a restore button.

**Alternatives considered:**
- Reuse `StageColumn` with a different layout — `StageColumn` is tightly coupled to kanban column layout, reuse would require significant refactoring.
- Virtual list for large archives — premature optimization; client-side filtering of archived issues is sufficient for expected volumes.

### D5: New hooks in useQueries.ts

Add three items to `useQueries.ts`:
- `useArchivedIssues()` — query for `GET /api/issues?archived=true`
- `useArchiveIssue()` — mutation for `POST /api/issues/:number/archive`
- `useUnarchiveIssue()` — mutation for `POST /api/issues/:number/unarchive`

All mutations invalidate `['issues']` and `['archived-issues']` on success to keep both views in sync.

### D6: KanbanBoard passes archived count to Done column

`KanbanBoard` will call `useArchivedIssues()` (or receive archived count via prop from `KanbanView`). The simpler approach: `KanbanView` fetches both regular issues and archived issues, passing `archivedCount` to `KanbanBoard`, which forwards it to the Done `StageColumn`.

## Risks / Trade-offs

- [API not yet available] → All archive API calls should gracefully handle errors (the `ApiError` class already exists). If #101 endpoints are not deployed, archive buttons will show error toasts but won't break the page.
- [Two issue fetches on kanban page] → Regular issues + archived issues are separate queries. Accepted trade-off: archived query is lightweight (only needed for count), and React Query caches/deduplicates.
- [Archived count staleness] → After archive/unarchive, both `['issues']` and `['archived-issues']` query keys are invalidated, ensuring freshness.

## Migration Plan

No migration needed. This is additive — all changes are new components, new routes, and new API methods. Existing kanban behavior is unchanged for non-archived issues.

## Open Questions

None.
