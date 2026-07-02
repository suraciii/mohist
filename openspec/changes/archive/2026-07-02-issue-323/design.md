## Context

Insights M1 (#322) shipped the conclusions-first **Signal Summary** plus a deliberately empty `ChartPlaceholder` zone (`data-future="charts-m2"`). All nine trending charts + `EpicProgressList` still live on the Dashboard, composed by `ProductivityZone` (`pages/dashboard/productivity/ProductivityZone.tsx`) into a flat, unfocused scroll. M2 closes the loop: relocate every one of those visualizations onto `/insights` below the Signal Summary, regroup them under four question-led dimension headings, and remove the Dashboard Productivity zone so the Dashboard returns to attention/pulse and `/insights` becomes the single retrospective space.

Today's mount graph (all under `pages/dashboard/`):

- `pages/dashboard/charts/` — generic SVG primitives (`ChartContainer`, `ChartAccessibility`, `ChartAxes`, `AreaSeries`/`BarSeries`/`LineSeries`/`ScatterSeries`/`SegmentedBarSeries`, `useReducedMotion`).
- `pages/dashboard/productivity/` — the 10 composed components + `model/` (`throughput`, `delivery-time`). `ProductivityZone.tsx` mounts them flat: `EpicProgressList, CompletionTrend, ThroughputChart, CycleTimeChart, StageDurationChart, CumulativeFlowChart, QualityPanel, FtrTrendChart, InvestmentPanel, CostTrendChart`.
- Each component calls its **own** data hook (`useCompletionThroughput`, `useCompletionTrend`, `useDeliveryTime`, `useCumulativeFlow`, `useStageDuration`, `useQualityMetrics`, `useAgentUsage`, `useCostRollup`, `useEpics`) — there is no shared parent fetch.

Per-chart data time windows (already returned by each endpoint, M2 only *labels* them):

| Component | Hook | Window source | Label |
|---|---|---|---|
| ThroughputChart | `useCompletionThroughput` (`bucket=day`) | fixed endpoint window | `30d` |
| CompletionTrend | `useCompletionTrend` (`bucket=week`) | fixed; already renders "{n} weeks" meta | `12w` (existing meta covers it) |
| CumulativeFlowChart | `useCumulativeFlow` | server `rangeFrom`/`rangeTo` | derived |
| CycleTimeChart | `useDeliveryTime` | fixed endpoint window | `30d` |
| StageDurationChart | `useStageDuration` | server `window.from`/`to` | derived |
| QualityPanel | `useQualityMetrics` | already titled "Last 7 days"/"Last 30 days" | existing copy covers it |
| FtrTrendChart | `useQualityMetrics` | server `trend.from`/`to` | derived |
| InvestmentPanel | `useCostRollup` | already states "cumulative across project history" | existing copy covers it |
| CostTrendChart | `useAgentUsage` | server `rangeFrom`/`rangeTo` | derived |
| EpicProgressList | `useEpics` | none — live snapshot | exempt |

Constraints:
- **Pure migration + regrouping.** No new chart, no chart internal rendering/data-fetch/interaction/empty-state/a11y change, no new endpoint, no DTO/schema/API change, no new persisted data (confirmed: server impact is none).
- The M1 placeholder contract (`ChartPlaceholder`, `data-future="charts-m2"`) is superseded and removed.
- Web is FSD with no i18n (copy hardcoded inline); tests colocated; a11y segregated from default `npm test`.
- `prerequisite #322` (M1) is done; M2 may freely restructure the Insights page it introduced.

See `proposal.md` for motivation and `specs/{insights-charts,dashboard-shell,insights-signal-summary}` for formal requirements.

## Goals / Non-Goals

**Goals:**
- Render all migrated trend charts on `/insights` below the Signal Summary in **exactly four** fixed dimension groups (产出 / 交付效率 / 质量 / 投入), in that order, each under a question-led title.
- Assign each chart to its spec-mandated group; place `EpicProgressList` without disturbing the four groups.
- Annotate each migrated chart with its current data time window (from the endpoint/hook it already uses), without implementing a range selector.
- Remove the Dashboard Productivity zone entirely; charts become reachable **only** on `/insights`.
- Preserve every chart's interaction, empty-state, and accessibility behavior across the move.

**Non-Goals** (from issue/proposal/specs):
- No new chart, no chart-internal logic change, no time-range selector (M3), no epic/label drill-down.
- No backend / API / DTO / schema change.
- No Dashboard rebuild of any migrated chart; no fifth dimension group.

## Decisions

### D1 — Relocate chart components + primitives under `pages/insights/`, preserving the relative import graph

After migration the Dashboard no longer references any chart, so both `pages/dashboard/charts/` (primitives) and `pages/dashboard/productivity/` (composed components + `model/`) move to their sole consumer:

- `pages/dashboard/charts/` → `pages/insights/charts/` (generic SVG primitives, byte-for-byte).
- `pages/dashboard/productivity/` → `pages/insights/panels/` (the 10 components + `model/`).

Keeping `charts/` and `panels/` as **sibling** directories preserves every component's existing `../charts` import, so the only intra-component edits are the window-badge additions (D4) — the series/axes/accessibility wiring is untouched, minimizing regression risk and review surface. `ProductivityZone.tsx` + `ProductivityZone.test.tsx` are deleted (superseded by `InsightsCharts`, D2).

**Alternatives considered:**
- *Move primitives to `shared/ui/charts/`.* More FSD-"correct" for generic primitives and future-reusable, but it touches every primitive import site across all 10 components for no current second consumer — larger diff, more churn, against the "pure migration" boundary. Defer unless another consumer appears.
- *Leave components under `pages/dashboard/` and import them into Insights.* Rejected — the Dashboard no longer uses them; leaving them dashboard-named is misleading and contradicts the "reachable only on /insights" requirement.

### D2 — One `ChartGroup` wrapper + one declarative `InsightsCharts` composition (no per-group files)

Introduce two small components in `pages/insights/ui/`:

- `ChartGroup` — props `{ id, title, question, children }`; renders a `<section data-testid="insights-chart-group" data-dimension="<id>">` with a heading carrying the question-led title.
- `InsightsCharts` — renders **exactly four** `ChartGroup`s in the fixed order from a single declarative array, each mounting its assigned components. `InsightsPage` replaces `<ChartPlaceholder />` with `<InsightsCharts />` and drops the `ChartPlaceholder` import + file.

Group spec (id → question-led title → members):

| Order | id | Title / question | Members |
|---|---|---|---|
| 1 | `output` | 产出 — 你交付了多少？ | `EpicProgressList`, `ThroughputChart`, `CompletionTrend`, `CumulativeFlowChart` |
| 2 | `delivery` | 交付效率 — 多快？ | `CycleTimeChart`, `StageDurationChart` |
| 3 | `quality` | 质量 — 一次做对了吗？ | `QualityPanel`, `FtrTrendChart` |
| 4 | `investment` | 投入 — 花了多少？ | `InvestmentPanel`, `CostTrendChart` |

Encoding the four groups as one array makes "exactly four, fixed order, fixed membership" structural and trivially assertable (count groups, assert `data-dimension` order, assert each group contains its chart testids) — directly mapping to the `insights-charts` spec scenarios.

**Alternatives considered:**
- *Four separate group components (`OutputGroup`, …).* Duplicates the wrapper shell four times, makes "exactly four" a count-by-convention rather than a single data structure, and adds files for no behavioral reason. Rejected.
- *Keep the flat `ProductivityZone` order and just add headings.* Rejected — the spec mandates the four-dimension grouping and the specific membership/order, not the current flat sequence.

### D3 — `EpicProgressList` placed first in the 产出 group; exempt from the time-window annotation

`EpicProgressList` reads `useEpics` (a live in-progress snapshot) and has **no endpoint time window**, so it cannot carry a window label. The spec permits 产出-group or standalone-slot placement. Placing it **first in 产出** keeps exactly four groups (no fifth slot) and is the closest semantic fit (epic progress = output trajectory toward delivery). It is explicitly exempt from D4's per-chart window annotation; the `insights-charts` spec test asserts this exemption so it is not read as a missing label.

**Alternatives considered:**
- *Standalone slot above the four groups.* Adds a non-dimension section, complicates the "exactly four groups" test surface, and separates epic progress from the throughput/completion charts it naturally sits beside. Rejected.

### D4 — Time-window annotation as additive header chrome on the 6 charts that don't already show it; sourced from each chart's own data, no wrapper-level hook calls

Requirement: each migrated chart annotates its current data window. Three charts **already** display their window and need no new badge: `CompletionTrend` ("{n} weeks" meta ≡ the 12-week endpoint window), `QualityPanel` ("Last 7 days" / "Last 30 days" sub-titles), `InvestmentPanel` ("cumulative across project history" caliber label). For the remaining six, add a small purely-presentational badge to the existing header row (alongside the `<h3>`), tagged `data-testid="<chart>-window"`:

- `ThroughputChart` → `30d` (fixed; completion `bucket=day` trailing window).
- `CycleTimeChart` → `30d` (fixed; delivery-time endpoint trailing window).
- `CumulativeFlowChart` → derived from `data.rangeFrom`/`rangeTo`.
- `StageDurationChart` → derived from `data.window.from`/`to`.
- `FtrTrendChart` → derived from `trend.from`/`to`.
- `CostTrendChart` → derived from `data.rangeFrom`/`rangeTo`.

The badge lives in header chrome only — it does **not** touch chart-body rendering, data-fetch, lens toggles, overlays, empty states, or `ChartAccessibility` summaries, so "internal behavior unchanged" holds. Sourcing the label from the hook data the chart already consumes co-locates label-truth with the component that owns the data and avoids any duplicate hook call at the group/wrapper level. When a server-range chart is in its empty state (no data → no range), the badge is hidden, consistent with the chart's own empty handling.

**Alternatives considered:**
- *Wrapper-level labels (`InsightsCharts` calls each hook to read the window).* Duplicates data ownership, couples the group to every chart's hook, and for fixed-window charts would still need a constant map. Rejected.
- *A central `chartWindows` constant map consumed by the wrapper.* Same coupling, and splits window truth from the chart that fetches it. Rejected.

### D5 — Dashboard narrowing: remove the productivity zone slot + component; trim the zone type

- `DashboardPage.tsx`: drop `{ id: 'productivity', name: 'Productivity' }` from `DASHBOARD_ZONES`, remove the `ProductivityZone` branch from the zone `.map`, remove the import.
- `DashboardZone.tsx`: remove `'productivity'` from `DashboardZoneId`.
- Delete `pages/dashboard/productivity/ProductivityZone.tsx` + `.test.tsx`.

The surviving zones (pulse, digest) and the headline/`AttentionHero` hero are untouched. The zones grid goes from 3 → 2 children; `md:grid-cols-2` renders two items cleanly with no layout regression. No backend/API/data-contract change (asserted by the `dashboard-shell` "no backend changed" scenario).

### D6 — Test strategy: retire Dashboard productivity tests, add an `insights-charts` spec, flip the M1 placeholder assertion

- **Delete** `ProductivityZone.test.tsx` (component removed).
- **Update** `DashboardPage.test.tsx`: the "slot identities stable" test drops the productivity assertion and instead asserts `dashboard-zone-productivity` is **absent**; remove the `productivity.contains('productivity-zone'/'productivity-quality')` checks from the zone-content test. The existing empty-state test (already asserts productivity absent) and top-to-bottom order test are unchanged.
- **Add** `pages/insights/ui/InsightsCharts.test.tsx` (spec): exactly four groups in fixed `data-dimension` order; each group heading carries its question-led title; each group contains exactly its chart testids; placeholder absent; per-chart window badges present for the six D4 charts (and the existing-label charts shown to carry their window copy); **no** time-range selector control rendered anywhere.
- **Update** `InsightsPage.test.tsx`: the test asserting `insights-chart-placeholder` flips to assert the four chart groups render instead. The Signal Summary structure/empty/populated/graceful-degradation tests are unaffected (Signal Summary is unchanged by M2).
- **Per-chart no-regression parity** is carried by each component's existing colocated test (e.g. `ThroughputChart.test.tsx`), which moves with the component to `pages/insights/panels/`; the additive window badge uses a new testid and does not alter existing assertions.

## Risks / Trade-offs

- **[Import-path churn across 10 components + primitives]** → Mitigation: keep `charts/` and `panels/` as siblings so `../charts` keeps resolving; the only intra-component edits are the D4 badges. `npm run typecheck -w packages/web` catches any missed import.
- **[Dashboard grid shifts 3 → 2 zones]** → Mitigation: `md:grid-cols-2` already lays out two items correctly; verify via the surviving layout test (headline → hero → zones order unchanged).
- **[Server-range window badge absent in empty state]** → Mitigation: hide the badge when the range is null (the chart body is already replaced by its empty state then); test the badge on the populated path only. Not a regression — empty behavior is unchanged.
- **[EpicProgressList has no window — could be misread as a missing annotation]** → Mitigation: D3 documents the exemption and the spec test asserts it explicitly.
- **[`CompletionTrend` "{n} weeks" meta vs a `12w` badge]** → These answer different questions (buckets actually rendered vs the endpoint's fixed window); keep the existing meta as the annotation and do not add a redundant badge. Low confusion risk since the meta count is data-derived.

## Migration Plan

**Deploy order:** web only (no server change). No database migration, no API/contract change.

1. **Move** `pages/dashboard/charts/` → `pages/insights/charts/` and `pages/dashboard/productivity/` → `pages/insights/panels/` (preserving `../charts`); move the 10 colocated `*.test.tsx` with their components.
2. **Add** `ChartGroup` + `InsightsCharts` in `pages/insights/ui/`; wire the four groups and the D4 window badges.
3. **Update** `InsightsPage.tsx` to render `<InsightsCharts />` and delete `ChartPlaceholder.tsx` + its `index.ts` export.
4. **Narrow** the Dashboard per D5; delete `ProductivityZone.{tsx,test.tsx}`.
5. **Update tests** per D6; add `InsightsCharts.test.tsx`.
6. **Validate** — `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` must pass.

**Rollback:** revert the web commit. Since no backend/persisted state is involved and the change introduces no data, rollback simply restores the Dashboard Productivity zone and the Insights placeholder. There is nothing to undo server-side.

## Open Questions

- **Group heading copy:** the proposed question-led titles (产出 — 你交付了多少？, etc.) are placeholders pending the final UX wording; they are pure inline copy behind the `ChartGroup` title prop, so any later change is copy-only with no structural impact.
- **Window-badge format for derived ranges:** show a compact span (e.g. `from–to` dates) or a day-count (e.g. `90d`)? The spec only requires the annotation be *derived from the returned range*; implementation can pick the more readable form and pin it in the spec test. Lean toward the date span since server ranges (esp. cumulative-flow) are not always a clean round number of days.
