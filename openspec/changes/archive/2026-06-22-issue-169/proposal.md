## Why

The Dashboard already ships four zone mount points (`Attention`/`Pulse`/`Productivity`/`Digest`) as empty placeholders, but a returning user has no "did I get things done this week?" payoff on the default landing — the Productivity slot is still a dashed empty box. The data sources this view needs are now complete (C: completion snapshot + time series in #165; D: agent/session usage in #166; Epic progress via the existing `useEpics()`). This issue composes those sources into the Productivity zone so the Dashboard delivers the satisfaction signal ("completed count + Epic progress fill + an upward trend curve") that Epic #9 promised, without adding any new backend query.

## What Changes

- Fill the Dashboard `productivity` zone slot (currently an empty `DashboardZonePlaceholder`) with a real Productivity zone view that composes three existing data sources and adds no new domain query.
- Render a **snapshot row** from C: this week's `completed` / `failed` / `new` counts (from `useCompletionSnapshot`).
- Render **Epic progress bars** from `useEpics()`: show at least 2 in-progress Epics using `progress.deliveredCount / progress.totalIssueCount`; render an empty state when fewer than 2 in-progress Epics exist.
- Render a **completion trend** visualization from C's time-series aggregation endpoint (by-week buckets); use lightweight SVG / existing primitives and do **not** introduce a charting library.
- Render an **"投入" (investment)** section sourced from D (agent/session usage), collapsed by default; when expanded, annotate the caliber/basis of the numbers (e.g. token/cost window definition) so the figure is not misread.
- All data is read from C / D / `useEpics()`; no new server endpoint or persistence field is introduced by this issue.
- No streak, badges, ranking, cross-project aggregation, or configurable time range (Non-Goals).

## Capabilities

### New Capabilities

- `dashboard-productivity`: The composition UI that fills the Dashboard `productivity` zone — combines C's completion snapshot + time-series trend, D's agent/session usage (collapsed "investment"), and `useEpics()` Epic progress bars into a single satisfaction-oriented view with empty states. Owns layout, trend rendering (lightweight SVG), and the investment collapse/expand behavior; owns no data query.

### Modified Capabilities

- `dashboard-shell`: The `productivity` slot transitions from an empty placeholder to a mount point that renders the `dashboard-productivity` zone content. The existing "each placeholder SHALL be empty" / "no zone content is implemented" scenarios no longer hold for the Productivity slot (they remain in force for the other three slots until their downstream issues land).

## Impact

- **Affected code (web only)**:
  - `packages/web/src/pages/dashboard/ui/DashboardPage.tsx` — the `productivity` entry stops rendering `DashboardZonePlaceholder` and instead renders the new Productivity zone component (the other three zones stay as placeholders).
  - New Productivity zone component(s) under `packages/web/src/pages/dashboard/` (e.g. `ProductivityZone.tsx` plus small sub-components for snapshot row, Epic progress list, trend chart, investment panel).
  - New lightweight trend rendering primitive (SVG-based or composition of existing primitives); no charting dependency added.
- **Consumed contracts (unchanged)**:
  - C: `useCompletionSnapshot` / `deriveCompletionSnapshot` at `packages/web/src/entities/issue/lib/completion-snapshot.ts:29` and the Issue-context time-series aggregation endpoint (by-week buckets) from #165.
  - D: agent/session usage snapshot + time-series introduced by #166 (consumed read-only; caliber annotated in UI).
  - `useEpics()` → `EpicWithProgress[]` with `progress.deliveredCount / progress.totalIssueCount` at `packages/web/src/entities/epic/api/queries.ts:3`.
- **Dependencies**: no new npm dependency; no new server route; no persistence change.
- **Tests**: new Dashboard tests covering snapshot count rendering, ≥2 Epic progress bars (and the <2 empty state), trend visualization presence, investment section default-collapsed + caliber annotation on expand, and that all data is sourced from the existing hooks (no new fetch).
- **Risk (low)**: pure composition UI; data contracts are owned by C/D. If D's aggregated usage surface is not yet available at implementation time, the investment section degrades to a labeled empty state while keeping its collapsed-by-default + caliber-annotation structure wire-ready; the D hook is plugged in once available, per the issue's risk note.
