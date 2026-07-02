## Why

Insights M1 (issue #322) shipped the conclusions-first Signal Summary, but left a deliberately empty chart-placeholder zone below it: when an operator wants the evidence behind a verdict (e.g. "质量下降了" — how exactly), there is no chart to expand. Those visualizations still live on the Dashboard's Productivity zone, which has grown into a long, unfocused scroll that mixes "what needs my attention" with "how am I trending." M2 closes the loop by relocating every trending chart to `/insights` — where the verdict already lives — and reorganizing them under question-led group headings, so the Dashboard returns to attention/pulse and Insights becomes the single retrospective space. This is needed now because the placeholder contract established in M1 explicitly names M2 as the chart-migration deliverable.

## What Changes

- Migrate every chart currently rendered by the Dashboard Productivity zone onto `/insights`, below the Signal Summary, replacing the M1 chart-placeholder zone. The migrated set (9 charts + EpicProgressList): `ThroughputChart`, `CompletionTrend`, `CumulativeFlowChart`, `CycleTimeChart`, `StageDurationChart`, `QualityPanel`, `FtrTrendChart`, `InvestmentPanel`, `CostTrendChart`, and `EpicProgressList`.
- Reorganize the migrated charts into exactly four dimension groups, each under a heading that states the question the group answers:
  - **产出 (Output)** — `ThroughputChart` / `CompletionTrend` / `CumulativeFlowChart`
  - **交付效率 (Delivery Efficiency)** — `CycleTimeChart` / `StageDurationChart`
  - **质量 (Quality)** — `QualityPanel` / `FtrTrendChart`
  - **投入 (Investment)** — `InvestmentPanel` / `CostTrendChart`
- `EpicProgressList` migrates with the others; its group placement (产出 group or a standalone slot) is resolved in design — the four dimension groups above remain fixed regardless of that decision.
- Each migrated chart annotates its current data time window (e.g. "30d", "7d", "90d") derived from the window its existing endpoint/hook already returns, since M3 (time-range selector) is out of scope.
- **BREAKING (UI only, no API/schema break):** the Dashboard no longer renders a Productivity zone — the `ProductivityZone` component and its zone slot are removed. Charts are no longer reachable on the Dashboard; they live exclusively on `/insights`. No backend, API, or data-contract change is involved.
- Pure migration + reorganization: no new chart is introduced, no chart's internal rendering/data-fetch logic is altered, and every chart's existing fixed time window is preserved.

## Capabilities

### New Capabilities

- `insights-charts`: The migrated trend charts rendered on `/insights` below the Signal Summary, organized into the four fixed dimension groups (产出 / 交付效率 / 质量 / 投入), each group headed by a question-led title, and each chart annotated with the data time window its existing endpoint already yields. Covers the group structure, the placement of `EpicProgressList`, the per-chart time-window label, and the no-regression contract (interactions, empty states, accessibility preserved from the Dashboard originals).

### Modified Capabilities

- `insights-signal-summary`: The chart-placeholder requirement added in M1 is removed — the placeholder zone is replaced by the real `insights-charts` groups. The four verdict sentences, their trend/magnitude derivation, and their graceful-degradation behavior are unchanged; only the "placeholder, no charts rendered" contract is superseded.
- `dashboard-shell`: The Dashboard composition is narrowed to remove the Productivity zone. The Dashboard SHALL NOT render a productivity/charts zone; trending visualizations live exclusively on `/insights`. The pulse and digest zones (and the headline/hero) are unaffected.

## Impact

- **Web** (`packages/web`):
  - `pages/insights/ui/InsightsPage.tsx` — replace `<ChartPlaceholder />` with the four-group chart layout; remove `ChartPlaceholder.tsx`.
  - New group/section composition under `pages/insights/` (or a shared location) that mounts the existing chart components, which move out of `pages/dashboard/productivity/`. The reusable chart primitives in `pages/dashboard/charts/` move alongside or to a shared charts module (their internal logic is untouched).
  - `pages/dashboard/ui/DashboardPage.tsx` + `DashboardZone` — drop the `productivity` zone id from `DASHBOARD_ZONES` and remove `ProductivityZone` mounting; the `pages/dashboard/productivity/ProductivityZone.tsx` file is deleted.
  - Per-chart time-window label wiring (windows already known per hook: throughput 30d, completion-trend 12w, delivery-time 30d, quality 7d+30d windows + trend range, cumulative-flow / stage-duration / agent-usage server-provided ranges).
  - `pages/insights/ui/ChartPlaceholder.tsx` and its `data-future="charts-m2"` marker are removed.
- **Server** (`packages/server`): none — no endpoint, DTO, or schema change. All data hooks are reused as-is.
- **Tests** (`packages/web`):
  - Remove/retire `ProductivityZone.test.tsx` and Dashboard assertions for the migrated charts; update `DashboardPage.test.tsx` to assert the productivity zone is gone.
  - Add an `insights-charts` spec test covering the four groups, group titles, per-chart time-window labels, full chart population, and empty-state/no-regression parity.
  - Update the M1 Insights test that asserted the placeholder to assert the chart groups instead.
  - typecheck + test (`npm run typecheck -w packages/web`, `npm run test:run -w packages/web`) must pass.
- **Dependencies / systems**: none beyond the web package.
- **Risk** (low, carried from issue): pure frontend component relocation and re-mounting with no backend, schema, or API-contract change. The chief care item is preserving each chart's interaction, empty-state, and accessibility behavior across the move and adding the time-window labels without altering chart internals.
