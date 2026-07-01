## Why

The Dashboard can already show *how many* issues ship per day (throughput) and *what they cost* (cost trend), but it gives no feedback on *how long* an issue takes from start to finish or whether that delivery speed is stable. Without a cycle-time signal the operator cannot judge "how much will ship next week," nor tell whether the factory is getting faster or slower. This is possible now because the lifecycle events that define duration — issue creation, work start, and completion — are already persisted and read by the existing completion metrics path, so cycle/lead time can be exposed as a pure read with no new data collection; and the reusable Dashboard chart baseline established by the cost-trend chart is now in place for this chart to compose against.

## What Changes

- Add a **cycle-time scatter control chart** to the Dashboard Productivity zone: one point per delivered (done) issue, x = completion date, y = the issue's cycle time in days.
- Expose **two duration lenses on the same chart** — lead time (created → completed) and cycle time (first work-started → completed) — so the operator can separate queue/wait from active work.
- Overlay two **rolling percentile lines** — P50 (median) and P85 — to express "how fast most issues complete" and "how dispersed the tail is."
- Apply the **first-work-start-to-final-completion rule** for issues with multiple work attempts (retries / reruns): cycle time spans the earliest `IssueWorkStarted` to the final completion, not the latest attempt in isolation.
- Scope the chart to a **fixed trailing window** of completed issues (e.g. last 30/60 days); the window length is fixed, not user-configurable.
- Render full **loading / error / empty** states via the shared chart three-state wrapper; the empty state names a concrete next action (cycle time appears once an issue completes on the project).
- No grouping/coloring by label or epic, no scatter-point click-through drill-down, no outlier annotation (Non-Goals).

## Capabilities

### New Capabilities

- `dashboard-cycle-time`: The cycle-time scatter control chart mounted in the Dashboard Productivity zone — one point per delivered issue (y = cycle days), the lead-time vs cycle-time lenses, the overlaid rolling P50 and P85 percentile lines, the fixed trailing window, and its loading/error/empty states with a concrete empty-state next action. Composes against the existing `dashboard-charts` baseline (pinned library, theme tokens, three-state wrapper, accessibility wrapper, tabular-nums/motion conventions).
- `issue-delivery-time-metrics`: The server-side aggregation exposing per-completed-issue lead time (created → completed) and cycle time (first `IssueWorkStarted` → final completion), including the first-work-start-to-final-completion rule for issues with multiple work attempts. Derived purely from already-persisted lifecycle events (issue creation, `IssueWorkStarted`, terminal completion event) — no new events, no domain writes, no new data collection. Exposed as an additive project-scoped read surface consumed by the chart.

### Modified Capabilities

<!-- None. The delivery-time aggregation does not alter the persisted completion-time behavior of `issue-completion-timestamp` (which already records the latest terminal moment); it only reads lifecycle events. The chart composes against — but does not change the requirements of — `dashboard-charts`. -->

## Impact

- **Server** (`packages/server`): adds a project-scoped delivery-time aggregation that reads already-persisted issue lifecycle events (creation time, `IssueWorkStarted`, completion event) and emits per-completed-issue lead/cycle durations over a trailing window, applying the first-work-start-to-final-completion rule for retries. Reuses the existing event-stream reading pattern already used for completion metrics; no schema change, no new events, no domain writes.
- **Web** (`packages/web`): adds a cycle-time scatter control chart widget in the Productivity zone, consuming the new delivery-time surface; overlays rolling P50/P85 percentile lines computed from the returned per-issue series; routes loading/error/empty states through the shared chart three-state wrapper and composes against the pinned chart library, theme tokens, accessibility wrapper, and tabular-nums/motion conventions.
- **No changes** to runner, workflow engine, issue lifecycle transitions, or existing API contracts beyond the additive delivery-time read surface.
- **Tests**: backend lead/cycle duration computation including the retry (first-work-start → final-completion) rule, reopen/re-completion, and zero-attempt edge cases; chart scatter values sourced from the surface; P50/P85 rolling percentile computation; three-state rendering and accessibility (SR summary, non-color-only legend distinguishing lead vs cycle vs percentile lines); theme-token color usage and `prefers-reduced-motion`/transform-based motion.
