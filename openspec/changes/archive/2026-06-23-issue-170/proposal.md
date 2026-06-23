## Why

When a user returns to Mohist after hours or days away, they want a single-screen answer to "what happened while I was gone" — what completed, what failed, what got archived — without paging through the Activity event stream or scanning issue lists. The Dashboard already reserves a `Digest` zone slot (delivered by #163), but it renders an empty placeholder; there is no at-a-glance recent-history summary today, forcing users to reconstruct recent momentum from raw events.

## What Changes

- Render recent-history summary content into the Dashboard `Digest` zone slot, replacing its empty placeholder
- Show recently **completed** issues (top N) as jumpable rows: number + title + relative time
- Show recently **failed** issues (top N) as jumpable rows: number + title + relative time
- Show recently **archived** issues (top N) as jumpable rows: number + title + relative time
- Optional: show a top-N activity event summary sourced from the same SignalR events-hub that feeds the Activity page
- Render an empty state when there is no recent activity across the tracked categories
- All data is consumed read-only from existing sources (`useIssues`, `useArchivedIssues`, events-hub); no new backend endpoints

## Capabilities

### New Capabilities

- `dashboard-recent-digest`: Read-only composition that mounts into the Dashboard `Digest` zone slot, deriving top-N recently completed/failed/archived issues (and optional top-N activity events) from existing issue and event sources, rendering jumpable summary rows with relative timestamps and an empty state when no recent history exists.

### Modified Capabilities

- `dashboard-shell`: The Dashboard `Digest` zone slot transitions from rendering an empty placeholder to mounting the new `dashboard-recent-digest` zone content. The slot identity and the four-slot composition contract are unchanged; only the `digest` slot's rendered content changes from placeholder to real zone content (the contract explicitly deferred zone content to downstream issues).

## Impact

- **Frontend (web)**: New Digest zone view under `packages/web/src/pages/dashboard` (or a dedicated widget), composing `useIssues` / `useArchivedIssues` query results sorted by `updatedAt` / `archivedAt` and (optionally) events-hub activity. Dashboard page wiring updates the `digest` slot to render the new content instead of `DashboardZonePlaceholder`.
- **Reusable components**: May reuse `RecentCard` patterns from `packages/web/src/widgets/coder-session`; relative-time and jump-link affordances already exist in the codebase.
- **Data sources**: No changes to `useIssues`, `useArchivedIssues`, events-hub, or any backend API. Read-only consumption only.
- **Backend / API**: None — acceptance criterion explicitly forbids new endpoints.
- **Boundaries**: Pure Dashboard composition layer; does not touch the Issue/Activity domain aggregates or replace the Activity page (Activity remains the full event stream for debugging; Digest is a dashboard overview window).
