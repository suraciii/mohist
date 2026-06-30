## Why

The Dashboard's completion surface is a single weekly count plus a weekly line sparkline — the operator can read "how many shipped this week" but cannot see whether the daily ship rate is steady, accelerating, or decaying, nor how many daily terminations are failures. With no trend or volatility signal, the operator cannot judge whether the "factory" is producing healthily. This matters now because the per-day completion data (completed vs failed, bucketed by terminal-event time across a trailing 30 days) is already produced by the server and already consumed at weekly resolution; this issue exposes it at daily resolution so delivery-health trend is visible at a glance. It is a pure additive read — no new data collection and no server changes — and it composes against the chart baseline the cost-trend chart (issue 294) already established.

## What Changes

- Add a **daily delivery throughput bar chart** to the Dashboard Productivity zone: one bar per trailing day (30 days), bar height encoding that day's completed-issue count, sourced from the existing completion metrics endpoint at `bucket=day`.
- Stack a **failed segment** within each bar (darker) encoding that day's failed terminations (issues that entered `cancelled`), sourced from the same endpoint's per-bucket `Failed` count.
- Overlay a **7-day moving average** line across the completed series to smooth single-day spikes and reveal the real trend.
- Render **loading / error / empty** states through the shared dashboard chart three-state wrapper; the empty state names a concrete next action (throughput appears once an issue completes on the project), and never renders a bare empty axis.
- Compose against the existing dashboard chart baseline — the pinned first-party SVG kit, theme-token colors, the accessibility wrapper, `tabular-nums`, and `transform`-based motion that honors `prefers-reduced-motion`. No new chart library is introduced.
- Data is bucketed by **completion-event time** (`IssueWorkCompleted` / `IssueClosed`), which is already the endpoint's behavior — so a post-completion issue edit (comment, label, title) never moves a bar.
- No configurable time window (fixed 30 days), no bar-click drill-down, no weekly/monthly toggle (Non-Goals).

## Capabilities

### New Capabilities

- `dashboard-throughput-trend`: The daily delivery throughput bar chart mounted in the Dashboard Productivity zone — one bar per trailing day encoding completed with a stacked failed segment, a 7-day moving average overlay, and loading/error/empty states — composed against the dashboard chart baseline and reading the existing completion metrics endpoint at daily resolution. Purely read-only with respect to domain state.

### Modified Capabilities

None. The completion metrics endpoint (`GET /api/projects/{ref}/issues/metrics/completion?bucket=day`) already returns dense 30-trailing-day buckets carrying per-bucket `Completed` and `Failed`, bucketed by terminal-event time; the `dashboard-charts` baseline and `issue-completion-timestamp` requirements are unchanged and only composed against / consumed.

## Impact

- **Web** (`packages/web`): a new throughput chart widget mounted in the Productivity zone as a sibling to `CompletionTrend` and `CostTrendChart`; a daily-bucket completion query hook (the existing weekly hook's daily analog); a client-side 7-day moving average derivation over the returned completed series. Reuses the shared chart kit (`ChartContainer`, `ChartAccessibility`, `BarSeries`, `ChartAxes`, `ChartLegend`), theme-token colors, and existing motion conventions. No new dependency.
- **Server** (`packages/server`): none. The completion metrics endpoint already returns dense 30-trailing-day buckets with per-bucket `Completed` and `Failed`, bucketed by terminal-event time.
- **No changes** to runner, workflow engine, issue lifecycle, events, or any existing API contract.
- **Tests**: three-state rendering and the accessibility wrapper (SR summary, non-color legend); bar completed/failed values sourced from the daily buckets; 7-day moving average values; theme-token color usage; empty-state next action; read-only (no domain writes).
