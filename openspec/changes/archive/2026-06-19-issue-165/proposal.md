## Why

The Dashboard's Productivity zone (issue G) needs *completion facts* to answer "how many issues completed / failed / were added this week, and how is the trend moving over time?" Today the Issue context exposes only per-issue reads (`IssueQuerier.ListAsync`) — there is no derived completion view, so counts must be hand-rolled on the client, and there is **no time-series aggregation at all**. Worse, the only available timestamp (`updatedAt`) is imprecise for completion: `Issue.cs` touches `UpdatedAt` on *every* change, so issues edited this week but completed last week get miscounted. A backend aggregation path with correct completion-time semantics must exist before the Productivity view (G) can trust its numbers — and the client snapshot must be replaceable by that path once ready.

## What Changes

- Add a **client snapshot derivation** (new standalone, replaceable function in `packages/web/src/entities/issue/`) that consumes `useIssues()` and derives last-7-day completed / failed / new counts from `status` + `createdAt` / `updatedAt`. It accepts `updatedAt` as an approximation but is isolated behind a stable signature/location so it can be swapped for an endpoint call once the backend is ready (reservation contract).
- Add a **server time-series aggregation endpoint** (new Issue-context query): returns completion-count buckets (by day / by week) using **correct completion-time semantics**, *not* `updatedAt`. Fixed bucket size for v1 (no configurable range/bucket size).
- **Explore in design**: whether issue/workflow persistence records a precise completion time. The Issue aggregate has no `CompletedAt` field today, so the endpoint likely must derive completion time from workflow run completion events rather than issue `updatedAt`.
- **BREAKING**: none — new read-only derivation and endpoint.
- Do **NOT** use `AgentActivity.summary.completed/failed` as the stats source: that is a current-activity-window count, not historical completion.

## Capabilities

### New Capabilities

- `issue-completion-metrics`: Derivation and query of issue completion counts (completed / failed / new) in the Issue bounded context. Owns both faces of the concern — the client snapshot contract (standalone, replaceable, last-7-day counts) and the server time-series aggregation endpoint (per-day / per-week buckets by correct completion-time semantics).

### Modified Capabilities

None. The existing `http-api` spec's requirements are unchanged; the new aggregation endpoint is introduced as part of the new `issue-completion-metrics` capability rather than altering existing API requirements. `dashboard-shell` is likewise untouched — this issue is a precursor to the Productivity zone (G) and does not fill that slot.

## Impact

- **Affected code**:
  - Client: `packages/web/src/entities/issue/` — new snapshot derivation function reading `useIssues()` + `Issue.status` / `createdAt` / `updatedAt` (`packages/web/src/entities/issue/api/queries.ts:26`, `packages/web/src/entities/issue/model/types.ts:147`).
  - Server: `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs` — new aggregation query (today only `ListAsync` / `ListWithLabelFiltersAsync`, no bucketing).
  - New HTTP route registered for the aggregation endpoint; path documented in the design for reviewer verification.
- **Persistence**: depends on issue/workflow completion-time semantics. `Issue.cs` has no `CompletedAt` — design must confirm whether to backfill from workflow run completion events or another durable source.
- **API**: new public read-only aggregation endpoint (non-breaking); the aggregation path correctness is the medium risk driver.
- **Not affected**: Productivity view UI (G), usage/cost metrics (D / Agent/Session context), `AgentActivity`.
