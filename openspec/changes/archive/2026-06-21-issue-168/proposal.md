## Why

When a workflow runs in the background, users have no glanceable answer to "is the pipeline alive, how much capacity is occupied, what is each active session doing, how much token is being burned" without leaving the Dashboard for the Activity page. The Dashboard already exposes an empty `Pulse` zone slot (issue #163) whose stated purpose is live pipeline health; filling it closes that gap with a read-only composite over data sources that already exist.

## What Changes

- Render a Pipeline Pulse view inside the Dashboard `Pulse` zone slot, replacing the current empty placeholder.
- Add a capacity header showing `active/max` slots plus an `active / waiting / completed / failed` status summary.
- Add compact active-session cards (a slimmed variant of `ActiveSessionCard`, not a full Activity-page replica) showing issue / stage / current task / task progress / token·cost / context-health color.
- Render an empty state when no sessions are active.
- Source all data from the existing `useAgentActivity` (`summary.slots` + `sessions[]`) and `useAgentStatus` hooks — no new query, no new backend endpoint, no domain-layer writes.

## Capabilities

### New Capabilities

- `dashboard-pulse`: Read-only Dashboard `Pulse` zone view that surfaces live pipeline health — slot capacity (`active/max`), status summary (`active/waiting/completed/failed`), compact active-session cards with context-health color, and an empty state — all derived from existing agent activity data sources.

### Modified Capabilities

- `dashboard-shell`: The `Pulse` zone slot is no longer required to render as an empty placeholder; its content is now governed by the `dashboard-pulse` capability while the slot identity and mount-point contract remain unchanged.

## Impact

- **Frontend only**: `packages/web/src/pages/dashboard/` gains a Pulse zone widget that mounts into the existing `pulse` slot in `DashboardPage.tsx`.
- **Reused code**: `useAgentActivity` / `useAgentStatus` (`packages/web/src/entities/agent/api/queries.ts`), `activity-cards.ts` (`sessionToCard` / `useActivityCards`), `SessionCard.tsx` (`ActiveSessionCard`), and `StatusBar.tsx` provide the data and rendering primitives.
- **No backend / API / domain-layer changes**: pure composite over existing read paths; no new endpoints, queries, or persistence.
- **No effect on Activity page**: the Activity page keeps its full session list; Pulse only shows a compact live summary.
