## Why

Epic has the bones of a complete lifecycle (Idle → Running ⇄ Paused → Done/Closed) plus a `RecordEvent` audit pipeline, but four real gaps make it unusable for everyday planning: a wrongly closed Epic cannot be recovered; issues must be linked one at a time; the list page cannot be searched or reordered; and the event pipeline drains to a no-op (`EpicGrain.ApplyPendingEvents` at `:601` calls `ClearPendingEvents`), so an Epic has no visible history. These gaps are surfaced now because users are moving real work into Epics and hitting each one in turn.

## What Changes

- Add a `Reopen` transition: `Done` or `Closed` → `Idle` (symmetric to Issue's `Cancelled → Backlog` reopen), recording an `EpicReopened`/status-changed event. Terminal states stop being irreversible.
- Add batch link/unlink: a single endpoint accepting an array of issue numbers/ids, replacing the N-round-trip pattern required today by the single-issue `EpicIssueRequest`.
- Make the Epic list queryable: `EpicQuerier.ListAsync` accepts an optional title-search term and a sort selector (priority / updated-at, asc/desc); the list endpoint forwards query params; the web list page gains a search box and sort control.
- Persist Epic events and expose an activity timeline: `ApplyPendingEvents` writes to a new Epic-events store instead of draining; a read path + endpoint serves them; the detail page renders a timeline of status changes, issue link/unlink, and priority changes.
- New `EpicReopened` event variant added to the `EpicEvent` union (the existing `EpicStatusChanged` already covers the status facet, but a dedicated reopen variant mirrors `IssueReopened` and lets the timeline distinguish "reopened" from generic status churn).

## Capabilities

- `epic-lifecycle-reopen`: Recovering an Epic from a terminal (`Done`/`Closed`) state back to `Idle` — the domain transition, its guards (only from terminal; non-terminal is a no-op or rejected), the grain entry point that re-establishes active issue memberships released on terminalization, the HTTP route, and the web detail-page action.
- `epic-batch-membership`: Linking and unlinking multiple issues to an Epic in a single request — batch grain operations honoring the same cross-epic active-membership uniqueness invariant as single link, the HTTP endpoints accepting issue-number arrays, and partial-failure semantics.
- `epic-list-query`: Searching Epics by title and ordering the list by priority or updated time in either direction — the querier query/sort parameters, the list-route query-string contract, and the web list-page search/sort controls.
- `epic-activity-timeline`: Persisting Epic domain events and rendering them as a chronological activity timeline — the events store + migration, the no-op-to-real `ApplyPendingEvents` persistence, the read path/endpoint, and the web timeline component covering status changes, issue link/unlink, and priority changes.

## Impact

- **Server domain**: `Epic/Domain/Epic.Transitions.cs` (new `Reopen`), `EpicLifecycleExceptions.cs` (reopen guard exception), `Epic/Domain/Events/EpicEvent.cs` (new `EpicReopened` variant), `Epic/Domain/Epic.cs` (event-list plumbing if needed for persistence).
- **Server grains**: `Epic/Grains/IEpicGrain.cs` + `EpicGrain.cs` — `ReopenAsync` (re-establish active memberships via `EpicActiveIssues` since terminalization released them), batch link/unlink methods, and replacing the `ApplyPendingEvents` no-op (`:601`) with real event persistence.
- **Server query/services**: `Epic/Services/EpicQuerier.cs` — `ListAsync` gains search/sort params; new event-read method; `Epic/Services/EpicDtos.cs` — event DTOs.
- **Server API**: `Api/EpicRoutes.cs` — `POST /{id}/reopen`, batch link/unlink endpoints, query params on `GET /`, and `GET /{id}/events`.
- **Server storage**: new EF Core migration adding an Epic-events table (column set analogous to the existing issue-events store).
- **Web**: `pages/epics/ui/EpicListPage.tsx` (search + sort controls), `pages/epic-detail/ui/EpicDetailPage.tsx` (reopen action + timeline section), `pages/epic-detail/model/primaryLifecycleAction.ts` (reopen for terminal states), `entities/epic` hooks/queries for reopen, batch membership, and events.
- **No CLI / runner changes**; no breaking API changes — all additions are new routes/params/fields. Existing single-issue link/unlink endpoints remain.
