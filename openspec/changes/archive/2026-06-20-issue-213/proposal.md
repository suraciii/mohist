## Why

Runner state today has no home of its own: it lives as an embedded section on the Activity page, mixed with session cards. A user with a few runners cannot glance at who is idle, busy, stale, or offline, and there is no terminal path to that answer without opening the browser. Runners need a dedicated, read-only listing surface in both the Web UI and the CLI so their status is one glance away.

## What Changes

- Add a dedicated **Runners** page in the Web UI, reachable from primary navigation, scoped to the current project (including global runners).
- The page shows a top summary bar counting runners by status (`idle` / `busy` / `stale` / `offline`).
- The page supports a scope filter: **all / global / this project**.
- Each row surfaces runner id, kind, status badge, scope, capacity usage (`used/total` slots), heartbeat freshness, and hostname. Offline / stale runners remain listed and are distinguished only by their status badge.
- An empty state (no runners) shows the runner start command hint.
- Remove the embedded runner list from the Activity page so Activity returns to a pure session view; keep the runner overview badge in the Activity status bar as a global quick indicator, and add a link from Activity to the Runners page.
- Add a `mo runner list` CLI subcommand that prints the runner list as a table with color-coded status, supports project and scope filtering, and shows the start command hint when no runners exist.

## Capabilities

### New Capabilities

- `runner-listing`: Read-only runner observation surface spanning the Web UI and CLI. Covers the dedicated Runners page (navigation entry, status summary bar, scope filter, per-row fields, offline-runner visibility, empty state with start hint) and the `mo runner list` subcommand (table output, color-coded status, project/scope filters, empty state with start hint). Both surfaces share one contract: the `idle`/`busy`/`stale`/`offline` status taxonomy, scope semantics (all/global/this-project), and the row field set, all backed by the existing `GET /api/projects/{projectRef}/runners` endpoint.

### Modified Capabilities

_None._ The backend runner-status API already exposes the full field set the listing needs, and the runner section currently embedded in the Activity page is an implementation detail not governed by an existing spec requirement, so relocating it is not a spec-level modification.

## Impact

- **Web UI**: new `Runners` route + nav entry in `AppSidebar`; a new page reusing/relocating the existing `runner-status` widgets (`RunnerList`, `RunnerSummary`) and `entities/runner` query; `ActivityPage` drops its `RunnerListCard` section and gains a link to `/runners`; the `RunnerSummaryBadge` in the Activity status bar is retained.
- **CLI**: new `list` subcommand under the existing `mo runner` command group (`MohistCliCommands.Server.cs` `RunnerCommands`), consuming the existing runner-status HTTP endpoint via `MohistCliApi`; table rendering via the existing `TableRenderer`, with status color coding.
- **Server API**: no change — `RunnerStatusRoutes` (`GET /api/projects/{projectRef}/runners`) and `RunnerStatusView` already carry id, kind, hostname, scope, status, capacity, heartbeat, and connection state. Scope/status aggregation is computed client-side.
- **Out of scope** (per Non-Goals): runner control actions (deregister/reconnect/pause), execution history or trend stats, single-runner detail view, and real-time log streaming.
