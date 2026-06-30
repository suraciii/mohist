## Why

The Dashboard surfaces spend as two isolated scalars — cumulative `totalCost` and `todayCost` — so the operator cannot see whether daily spend is rising, or whether cost-per-ship is improving as output scales. This matters now because the per-session usage timeseries and the cost-per-ship rollup are already recorded and queryable, so the trend can be exposed as a pure read with no new data collection. As the first chart on the Dashboard, this issue also fixes the reusable chart baseline (library, theme tokens, three states, accessibility) that every later chart issue will compose against — deferring it would force each subsequent chart to re-litigate the same decisions.

## What Changes

- Add a **daily cost bar chart** to the Dashboard Productivity zone: one bar per trailing day, height = that day's token cost, drawn from the existing agent usage timeseries.
- Overlay a **cost-per-ship trend line**: cumulative spend ÷ cumulative shipped-issue count, evaluated as of each day, expressing whether unit-delivery cost is rising or falling as output scales.
- Render full **loading / error / empty** states for the chart; the empty state names a concrete next action (no spend recorded yet — appears once an agent session reports usage).
- Establish the **dashboard chart baseline** that all future charts reuse:
  - Pin a single chart library for the whole Dashboard.
  - Drive all chart colors from theme tokens; retire hardcoded widget colors on the chart surface.
  - Provide a shared three-state (loading/error/empty) chart wrapper.
  - Provide an accessibility wrapper: a screen-reader data summary and a legend that does not rely on color alone.
  - Numeric labels use `tabular-nums`; bar-height changes animate via `transform`, never layout properties; honor `prefers-reduced-motion`.
- No per-session or per-issue cost breakdown, no budget-threshold alerts, no multi-currency switching (Non-Goals).

## Capabilities

### New Capabilities

- `dashboard-charts`: The reusable Dashboard chart baseline — the single pinned chart library, the theme-token color contract, the shared loading/error/empty three-state chart wrapper, and the accessibility wrapper (screen-reader data summary, color-non-only legend encoding), plus the numeric (`tabular-nums`) and motion (`transform`-based, `prefers-reduced-motion`-aware) conventions. Future chart issues compose against this.
- `dashboard-cost-trend`: The daily cost bar chart with an overlaid cost-per-ship trend line mounted in the Dashboard Productivity zone, including its loading/error/empty states and its empty-state next action. Consumes the existing agent usage/cost data.

### Modified Capabilities

- `agent-cost-metrics`: The cost-per-ship trend line is a cumulative-to-date ratio evaluated *per day* across the trailing window. The existing surface exposes a single cumulative `costPerShip` and a 7-day daily cost bucket series, but not the per-day cumulative ratio (nor the cumulative shipped-issue count per day). The agent usage/timeseries surface gains a per-day cumulative series so the trend renders faithfully; the existing `totalCost`/`todayCost`/`costPerShip`/`doneIssuesCount` rollup and 7-day daily-bucket series contracts are otherwise unchanged, and no new data collection is introduced.

## Impact

- **Web** (`packages/web`): adds a single pinned chart library dependency; new shared chart components (three-state wrapper, accessibility wrapper, theme-token color tokens) under the dashboard area; a new daily-cost trend widget mounted in the Productivity zone; hardcoded colors are retired on the chart surface. The existing `InvestmentPanel` scalar figures remain.
- **Server** (`packages/server`): extends the project agent-usage surface to expose the per-day cumulative cost-per-ship series needed by the trend (cumulative spend and cumulative shipped-issue count, or the derived ratio, per day). Computed purely from already-recorded per-session `UsageSummary` and issue status — the same sources as the existing rollup and timeseries; no new events, no domain writes, no new data collection.
- **No changes** to runner, workflow engine, issue lifecycle, or existing API contracts beyond the additive cumulative series.
- **Tests**: three-state rendering and the accessibility wrapper (SR summary, non-color legend); cost bar values sourced from the timeseries; trend line values sourced from the cumulative series; theme-token color usage; `transform`-based motion and `prefers-reduced-motion`; backend cumulative-series computation including zero-sample/empty cases.
