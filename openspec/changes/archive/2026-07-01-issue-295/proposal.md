## Why

The Dashboard tells the operator *how long* an issue takes end-to-end (cycle time scatter) and *how much* it costs, but never *where the time goes*: there is no per-stage (plan / build / check / integrate) duration view and no separation of active work from waiting. Without that, the operator cannot tell whether the bottleneck is a slow stage, an approval gate, or idle gaps — and "扩大规模" becomes a guess. The data is already recorded (every stage emits `StageStarted` / `StageCompleted`, approvals emit request/resolve, lifecycle emits work-start/complete), so this is a pure read over existing events with no new collection. It is the last piece needed to make flow visible end-to-end.

## What Changes

- Add a **stage-duration distribution chart** to the Dashboard Productivity zone: one horizontal bar per workflow stage, length = average (or median) time issues spend in that stage, derived from `StageStarted` → `StageCompleted` across delivered issues in a fixed trailing window.
- Surface a **flow-efficiency ratio** (active-work time ÷ total cycle time) next to the bars, expressing how much of an issue's cycle is actually working vs. waiting.
- Surface a **wait breakout** separately — at minimum approval-gate wait (approval request → resolve) and inactive/no-activity gaps — so the operator sees *why* flow efficiency is what it is. Pending (`awaiting`) approvals are excluded from the wait aggregate, consistent with `approval-waiting-metrics`; they already surface as attention items.
- Reuse the established **dashboard chart baseline** (`dashboard-charts`): pinned library, theme-token colors, shared three-state (loading/error/empty) wrapper with a concrete empty next action, accessibility wrapper (screen-reader summary, non-color-only legend), and `tabular-nums` / `transform`-based motion honoring `prefers-reduced-motion`. No new charting dependency.
- **BREAKING**: none. The new chart and its backend surface are strictly additive reads.
- No per-issue stage-time drill-down, no per-label grouping, no budget/threshold alerts (Non-Goals).

## Capabilities

### New Capabilities

- `workflow-stage-duration-metrics`: The project-scoped, windowed, read-only backend surface that derives, purely from already-persisted events, (a) per-stage duration samples from `StageStarted` → `StageCompleted` aggregated to average/median per stage across delivered issues, with a defined multi-run/retry aggregation basis (latest-attempt-per-stage, matching the existing invalidate-on-restart idiom); (b) a flow-efficiency ratio (active-work time ÷ cycle time); and (c) a wait-time breakout (approval-gate wait from approval request/resolve events plus inactive gaps). Returns a defined empty result distinguishable from a genuine zero; introduces no new events, persisted fields, domain writes, or data collection.

- `dashboard-stage-durations`: The Dashboard Productivity-zone chart that visualizes the stage-duration distribution (horizontal bars per stage), the flow-efficiency ratio, and the wait breakout. Composes against the `dashboard-charts` baseline (no new library), renders loading/error/empty states through the shared three-state wrapper with a concrete empty next action, and is purely read-only with respect to issue/session/workflow/approval state.

### Modified Capabilities

_None._ The new chart composes against the already-established `dashboard-charts` baseline (no baseline requirement changes) and the new backend surface re-derives its foundations from the same event records that back `issue-delivery-time-metrics` (cycle time) and `approval-waiting-metrics` (approval-gate wait) without altering those surfaces' contracts; those existing endpoints are unchanged.

## Impact

- **Server** (`packages/server`): a new additive project-scoped metrics read (alongside `IssueRoutes.DeliveryTimeMetrics` / `IssueRoutes.ApprovalMetrics`), computed in `IssueQuerier`-style fashion by scanning durable workflow/stage events + lifecycle/approval events already recorded. EF Core SQLite cannot translate `DateTimeOffset` against the TEXT columns, so aggregation is in-memory over the project's bounded issue set (existing idiom). No new events, no domain writes, no schema change.
- **Web** (`packages/web`): a new stage-duration widget mounted in the Productivity zone (next to `CycleTimeChart`), built on the existing `ChartContainer` / `ChartAccessibility` / `ChartAxes` / `ChartPrimitives` chart kit and a new `entities` query hook consuming the new endpoint. No new charting dependency.
- **No changes** to runner, workflow engine, issue lifecycle, or existing API contracts beyond the additive endpoint.
- **Tests**: backend aggregation (per-stage avg/median from paired stage events; multi-run/retry keeps latest attempt; flow-efficiency and wait-breakout math; empty vs. genuine-zero; window membership keyed on completion); frontend three-state rendering, accessibility wrapper, bar values sourced from the surface, theme-token color usage, and `transform`-based / `prefers-reduced-motion`-aware motion.
