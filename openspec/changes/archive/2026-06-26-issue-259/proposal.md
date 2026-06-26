## Why

Opening the dashboard reveals an empty Pulse placeholder box where the answer to "is the factory turning, and what is each in-flight issue doing right now?" should be. The `PulseZone` widget (slot usage, status counts, real-time compact candidate cards with stage / progress / token / context health) is already built and tested but was never wired into the dashboard, so a user cannot confirm at a glance that agents are actively working or where each in-flight issue stands. Landing it now delivers the dashboard's core "trust" signal once the first-screen information architecture (#258) has settled the zone layout.

## What Changes

- Mount the already-built `PulseZone` (`widgets/dashboard-pulse`) into the dashboard `Pulse` zone slot, replacing the empty placeholder rendered by `DashboardZone`.
- Surface runner slot usage (active/max) and live status counts — active / waiting / completed / failed — as pills atop the zone.
- Render real-time compact candidate cards (`CompactSessionCard`): issue number, stage badge, title, token/cost usage, task progress bar, and context-health indicator, each linking to its issue detail.
- Cap visible cards at a fixed N and render an overflow link to the Activity page (`/activity`) for the remainder.
- Render a "No active sessions" empty state when no candidate cards exist.
- **No ETA prediction and no activity ticker** — these are explicitly excluded as false-value (LLM tasks are high-variance; a ticker is noise).

## Capabilities

### New Capabilities
- `dashboard-pulse`: The dashboard Pulse zone content — what it renders (runner slot usage, live status counts, real-time compact candidate cards with stage / progress / token / context health, fixed-N card cap with Activity-page overflow link, empty state), that it derives exclusively from existing live activity sources, and that it is mounted in the dashboard `Pulse` slot.

### Modified Capabilities
- `dashboard-shell`: The `Pulse` zone slot ceases to render as an empty placeholder. It SHALL now mount the `dashboard-pulse` zone content; only the `Productivity` slot remains an empty placeholder.

## Impact

- **Code**: `packages/web/src/pages/dashboard/ui/DashboardPage.tsx` (wire `<PulseZone />` into the `pulse` zone branch, mirroring how `digest` mounts `DashboardDigestWidget`). The existing `widgets/dashboard-pulse` widget is mounted as-is with no behavioral edits.
- **No backend/API changes**: all data derives from existing live read-only sources (`useAgentActivity` via `useActivityCards`); no new endpoint, no write operations.
- **Dependencies**: Depends on #258 (dashboard first-screen layout) which is merged and provides the `pulse` slot mount point.
- **Tests**: Existing `PulseZone` / `CompactSessionCard` widget tests must continue to pass; new coverage required for the dashboard page mounting the Pulse zone (placeholder is replaced).
