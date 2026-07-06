## Context

The `/insights` page currently stacks a textual "Signal Summary" (four verdict
cards) above its charts. The summary derives precise figures
(e.g. "average cycle time 2.2h") from as few as `n=3` samples — most historical
completed issues lack a `work-started` event so `cycleDays` is `null` — while
the scatter plot below plots a different caliber in days. Two further caliber
mismatches erode trust in every number on the page:

- The "Cycle Time" card opens on a **lead-time** lens (`CycleTimeChart.tsx:84`
  seeds `useState<DurationLens>('lead')`), so the card title contradicts the
  data the default lens plots.
- The AI Quality card renders a fixed "Last 7 days" window
  (`QualityPanel.tsx:144`) alongside a range-sized window, while the server
  response field holding that range-sized window is named `window30d`
  (`IssueRoutes.Dtos.cs:308`) regardless of the selected range.

### Current state (verified by code reading)

**Signal Summary is fully page-internal.** The verdict model layer
(`pages/insights/model/{verdict,throughput,delivery,quality,investment,index}.ts`)
has no consumer outside `pages/insights` except `SignalSummary.tsx` and the
model's own unit tests. `pages/insights/index.ts` re-exports the verdict
symbols publicly, but no file beyond the page barrel imports them. The only
external assertion on the block is `App.test.tsx:195`
(`getByTestId('signal-summary')`). `formatCycleDays` — called out in the
proposal as a candidate formatting helper to retain — is in fact consumed
**only** by `SignalSummary.tsx`; `CycleTimeChart` has its own local
`formatDuration`, so `formatCycleDays` has no chart consumer and is dead once
the summary is removed.

**Chart hooks are decoupled from the summary.** The five page-level hook
*calls* in `InsightsPage.tsx:20-24` exist only to feed `SignalSummary`; every
chart component (`CycleTimeChart`, `QualityPanel`, `FtrTrendChart`, etc.) calls
its own hook independently with the same `range`. Removing the page-level calls
therefore does not starve any chart.

**Quality DTO is dual-window end-to-end.** `QualityMetricsResponse`
(`IssueRoutes.Dtos.cs:308`) carries `Window7d` + `Window30d`;
`IssueMetricsQuerier.GetQualityAsync` (`IssueMetricsQuerier.cs:451`) computes
both the fixed 7-day window and the range-driven primary window, plus a
range-scaled previous window and a range-scaled daily trend. The 7-day window
is the only piece that ignores the `range` parameter.

**Window indicators already exist on every chart except QualityPanel.**
`CycleTimeChart`, `StageDurationChart`, `ThroughputChart`,
`CumulativeFlowChart`, `FtrTrendChart`, and `CostTrendChart` each render a
`*-chart-window` badge. `QualityPanel` instead shows the window's date span as
the `QualityWindow` title — which becomes the panel's window indicator once the
panel collapses to a single window.

### Constraints / stakeholders

- The quality DTO change is a **breaking HTTP contract** (`GET /issues/metrics/quality`);
  server and web must land together. Risk rated **medium**.
- No runner / CLI / database / schema changes. No new endpoints.
- Aggregation algorithms (FTR classification, per-stage rework, ship-time
  windowing, trend bucketing) are preserved — only response shape and the
  fixed 7-day lens are removed.
- The page-level time-range selector stays a 7d/30d/90d switch; this change
  makes it the single source of window truth, it does not alter its UX.

## Goals / Non-Goals

**Goals:**

- **G1:** `/insights` renders no Signal Summary block; the chart region follows
  the range selector directly, with no conclusion-first subtitle.
- **G2:** The verdict-derivation model layer and the `SignalSummary` UI
  component are deleted; the five page-level metric hook *functions* remain
  available to the charts (only the page-level *calls* that fed the summary are
  removed).
- **G3:** The delivery-time scatter card's title matches its default lens, and
  a lens switch updates the title so the caliber is readable from the title
  area alone.
- **G4:** The AI Quality card renders exactly one range-driven window whose
  title shows its actual date span; the `GET /issues/metrics/quality` response
  carries a single primary window whose field name carries no fixed-day-count
  implication.
- **G5:** Every retained chart exposes a verifiable window indicator and
  enters its empty state on zero/sparse samples.

**Non-Goals:**

- Deleting or adding any chart (Cumulative Flow / Investment / Epic Progress
  trimming is a companion issue).
- Changing the time-range selector UX (still 7d/30d/90d).
- Changing any metric's aggregation algorithm or bucketing logic.
- Back-filling historical `work-started` events to thicken cycle-time samples
  (independent data-hygiene work).

## Decisions

### D1 — Signal Summary is a clean page-internal excision; hook functions stay

**Decision.** Delete, in one cut:

- `pages/insights/ui/SignalSummary.tsx` and its test references.
- The verdict model layer: `model/verdict.ts`, `model/throughput.ts`,
  `model/delivery.ts`, `model/quality.ts`, `model/investment.ts`, and the
  verdict-composer portion of `model/index.ts` (`deriveSignalSummary`,
  `SignalInputs`, `SignalSummaryModel`, the four per-dimension derivations,
  `deliverySlowestStageName`, `formatCycleDays`, the verdict helpers). Their
  unit tests (`*.test.ts` alongside each) are deleted with their subjects.
- `model/insights-range.ts` is **kept** (the charts and entity hooks import
  `InsightsRange`/`DEFAULT_INSIGHTS_RANGE` through it).
- In `InsightsPage.tsx`: drop the `insights-signal-section`, the `<SignalSummary>`
  usage + import, the five page-level hook *calls*
  (`completion`/`deliveryTime`/`quality`/`cost`/`stageDuration` locals), and
  the "先看结论，再看图表" subtitle. `<InsightsRangeSelector>` + `<InsightsCharts range>`
  remain; the charts-section `<h2>Charts</h2>` grouping header is kept.
- Prune `pages/insights/index.ts` to stop re-exporting the deleted verdict
  symbols and `SignalSummary`; keep the `InsightsPage` export and the
  `insights-range` re-exports.
- Update `App.test.tsx:195` and `InsightsPage.test.tsx` to drop the
  `signal-summary` / `insights-signal-section` assertions and the
  page-level-hook mock expectations that existed only to feed the summary.

**Rationale.** Code reading confirms the verdict layer has no consumer outside
`SignalSummary.tsx` and its own tests, so the cut has no external blast radius.
The chart components each invoke the metric hooks themselves, so removing the
page-level calls cannot change chart data. Deleting `formatCycleDays` alongside
the layer (rather than relocating it) is justified because no retained chart
consumes it — `CycleTimeChart` uses its own `formatDuration`. The proposal's
"relocate if still used by the charts" clause is conditional on a chart
consumer, and there is none.

**Alternatives considered.**

- *A1 — Keep the verdict model layer as dead code for a future summary.* Rejected:
  dead code rots and the spec mandates the layer's removal
  (`insights-page-composition`: "Page module does not consume the verdict layer").
- *A2 — Keep `formatCycleDays` in `model/delivery.ts` "just in case".* Rejected:
  no chart consumes it; `CycleTimeChart.formatDuration` already covers the
  chart-side need. Re-add only if a future chart explicitly wants the
  days→`Nd`/`Nh` formatting.

### D2 — Delivery card: dynamic title that tracks the lens; default lens stays `lead`

**Decision.** In `CycleTimeChart.tsx`, make the card `<h3>` title a function of
the active lens (`'lead'` → "Lead Time", `'cycle'` → "Cycle Time") so the title
and the plotted caliber always agree. Keep the default lens as `'lead'`. The
existing in-SVG axis label (`{lens === 'lead' ? 'Lead time' : 'Cycle time'}
(days)`) already tracks the lens and is left unchanged; the card title now
matches it.

**Rationale.** Lead time is always defined (`CreatedAt` is the aggregate's
immutable `init` field), whereas cycle time is `null` for most historical
issues (missing `IssueWorkStarted` events). Defaulting to the lens with the
denser, always-present sample population is the choice that lets the chart
" speak for itself with honest sample sizes" — the user sees real data on
first paint, and the empty-state path (`visibleCount === 0`) fires only when
genuinely nothing is plottable under the selected lens. A dynamic title is the
smallest change that satisfies both the default-alignment scenario and the
lens-switch scenario in `insights-delivery-time-chart`.

**Alternatives considered.**

- *A1 — Flip the default lens to `'cycle'` and keep the title "Cycle Time".*
  Rejected: cycle time is `null` for most historical issues, so the default
  paint would show a sparse/empty chart under the cycle lens — the opposite of
  "charts speak for themselves." This also would not fix the underlying
  title-vs-lens coupling; a future default flip would silently re-break it.
- *A2 — Rename the card to a neutral "Delivery Time" and rely on the in-SVG
  label alone.* Rejected: the spec requires the caliber to be readable from the
  *title area* without inspecting the lens toggle or the chart interior; a
  neutral title dodges the requirement rather than meeting it.

### D3 — Quality DTO: one range-driven primary window, field name `window`

**Decision.** Replace the dual-window contract with a single primary window on
both sides of the wire:

- **Server DTO** (`IssueRoutes.Dtos.cs`): `QualityMetricsResponse` becomes
  `(QualityMetricsWindowDto Window, double? PreviousFirstTimeRightRate, int
  PreviousSampleCount, QualityTrendDto Trend)`. The `Window7d`/`Window30d`
  fields are removed. `QualityMetricsWindowDto`, `QualityTrendDto`,
  `StageReworkRateDto` are unchanged.
- **Server querier** (`IssueMetricsQuerier.cs`): `QualityMetricsResult`
  becomes `(QualityMetricsWindow Window, QualityPreviousWindow PreviousWindow,
  QualityTrend Trend)`. `GetQualityAsync` drops the `window7d*` window
  computation and its `QualityAccumulator`; the primary window, the previous
  window, and the trend all continue to scale off `primaryDays =
  windowDays ?? 30`. The FTR classification, per-stage rework rate, ship-time
  windowing, and trend bucketing are untouched.
- **Server route** (`IssueRoutes.QualityMetrics.cs`): `BuildResponse` maps the
  single `Window` straight through; the `BuildWindow` helper is reused as-is.
- **Web DTO** (`entities/issue/api/quality-metrics.ts`):
  `QualityMetricsResponse` becomes `{ window: QualityMetricsWindowDto;
  previousFirstTimeRightRate?: number | null; previousSampleCount?: number;
  trend?: QualityTrendDto }`. `window7d`/`window30d` are removed.
- **Web panel** (`QualityPanel.tsx`): render exactly one `QualityWindow` sourced
  from `data.window`, titled with the existing `formatWindowTitle(window)`
  (which prints `from – to`). Drop the hardcoded `"Last 7 days"` label and the
  second `QualityWindow`. The panel-level empty state fires when
  `window.sampleCount === 0` (the per-window empty branch already exists and is
  preserved).

**Field-name choice — `window`.** The spec
(`insights-quality-window`: "field naming matches the actual caliber") forbids
a fixed-day-count name (no `window30d`) but does not mandate a particular
neutral name. `window` is chosen because (a) it is the natural singular for the
single primary trailing window the endpoint now returns, (b) it parallels the
existing `QualityTrendDto.from`/`to` pair that already describes the same span
without a day-count suffix, and (c) it is the lightest name that cannot drift
out of sync with the range. `previousFirstTimeRightRate` / `previousSampleCount`
keep their names: they describe a role (the immediately-preceding window of the
same length), not a day count, so they already satisfy the naming rule.

**Alternatives considered.**

- *A1 — Name the field `primaryWindow` to emphasize its role vs. the previous
  window.* Rejected: `previous*` already disambiguates the comparison window by
  role, and `window` vs. `previous*` is a clean role split without the extra
  qualifier. `primaryWindow` would also be a longer name for no added clarity.
- *A2 — Keep `window30d` as the field name but document that it holds the
  range.* Rejected: this is exactly the drift the spec forbids — a field name
  that implies a fixed day count while holding range-sized data.
- *A3 — Drop the previous-window comparison fields too, since the summary that
  consumed them is gone.* Rejected: the previous window is consumed by the
  trend's "is this getting better?" reading and is in scope for the FTR trend
  context; the spec explicitly keeps it
  (`insights-quality-window`: "Quality Trend and Previous-Window Comparison
  Scale With Range"). Only the fixed 7-day lens is removed.

### D4 — Window-indicator and empty-state floor: verify, do not re-architect

**Decision.** Treat the cross-cutting presentation floor
(`insights-chart-presentation`) as an audit + minimal gap-fill, not new
architecture:

- **Window indicator.** Every retained chart already exposes a `*-chart-window`
  badge driven by the selected range or by the response's own `from`/`to`:
  `CycleTimeChart` (`cycle-time-chart-window`, shows `{range}`),
  `StageDurationChart`, `ThroughputChart`, `CumulativeFlowChart`,
  `FtrTrendChart` (shows `trend.from – trend.to`), `CostTrendChart`. After D3,
  `QualityPanel`'s single window title (the `from – to` string from
  `formatWindowTitle`) serves as its indicator. No new badge is added; the spec
  allows "a date-range label, a range code, or a caption," and the panel's
  title is a date-range label.
- **Empty state.** `CycleTimeChart` (lens-aware `visibleCount === 0`),
  `StageDurationChart`, `ThroughputChart`, `FtrTrendChart`,
  `CumulativeFlowChart`, and `CostTrendChart` already enter empty on zero
  samples; `QualityPanel` after D3 enters empty when `window.sampleCount === 0`.
  `InvestmentPanel` and `EpicProgressList` are out of scope (companion issue).
  No chart renders a precise value without sample-size context: every rate card
  carries an `n=` sample counter, and every percentile/scalar empties out
  rather than fabricating a zero.

**Rationale.** The floor is already met by the existing chart code; the only
gap was the Quality card's dual-window + hardcoded-label presentation, which D3
closes. Adding a uniform `*-chart-window` testid to `QualityPanel` would be
cosmetic and is not required by the spec (the window title is a valid
indicator).

## Risks / Trade-offs

- **[Breaking HTTP contract on `GET /issues/metrics/quality`]** Any consumer
  reading `window7d`/`window30d` breaks at deploy. -> Code reading confirms the
  only web consumer is `QualityPanel.tsx` (and its tests) plus the
  `FtrTrendChart` test fixtures; `FtrTrendChart` itself reads only `data.trend`,
  which is unchanged. Server and web ship in one change; no third consumer
  exists in this repo. -> *Mitigation:* land server + web in the same PR; the
  typecheck on both packages is the integration gate.
- **[Stale `window7d`/`window30d` references in test fixtures]** Multiple web
  tests (`QualityPanel.test.tsx`, `FtrTrendChart.test.tsx`,
  `InsightsCharts.test.tsx`, `InsightsPage.test.tsx`,
  `quality-metrics.test.ts`) and server specs (`IssueMetricsApiSpecs.cs:
  RangeQuery_QualityEndpoint_*`) hardcode the old shape. -> *Mitigation:*
  rewrite each fixture alongside the DTO change; the typecheck catches any
  missed site, and the server spec rewrite is called out in the proposal.
- **[Loss of the "short vs. long term" comparison the 7-day window supported]**
  Removing the fixed 7-day window removes the only sub-range lens. ->
  *Mitigation:* the page-level range selector (7d/30d/90d) already provides
  short-vs-long comparison at the page level, and the FTR trend chart directly
  adjacent to the quality card visualizes within-window movement. This is the
  product shape the issue specifies.
- **[Dynamic card title may surprise users who memorized "Cycle Time"]** The
  card title now changes with the lens. -> *Mitigation:* the in-SVG axis label
  already behaved this way; the card title simply catches up. The lens toggle
  remains the explicit affordance, and the title is now consistent with it
  rather than contradictory.
- **[Dead exports left behind in `pages/insights/index.ts`]** If the verdict
  re-exports are not pruned, they become silent dead surface area. ->
  *Mitigation:* D1 explicitly prunes the barrel; the typecheck confirms no
  internal importer is broken.

## Migration Plan

- **No schema or storage migration.** No database, no migration, no new
  endpoint. The change is DTO-shape + presentation only.
- **Deploy order.** Server and web must deploy together because the quality DTO
  is a breaking change with no schema back-fill. In this monorepo both ship in
  one PR; `npm run typecheck -w packages/web` and `npm test` (server) are the
  integration gate.
- **Backward compatibility.** None provided. The repo is the only consumer of
  `GET /issues/metrics/quality` (no external clients), and the project is
  explicitly in active development with no version-compatibility obligation
  (per `AGENTS.md`). Old `window7d`/`window30d` clients are not supported.
- **Rollback.** Revert the commit. No persistent state is written by this
  change beyond normal metric reads; the DTO revert restores the prior shape
  byte-for-byte. In-flight page loads at deploy will simply re-fetch under
  whichever shape is current.

## Open Questions

- **Should the panel-level empty copy change?** Today both windows empty yields
  "No quality data yet — first-time-right and rework rates appear once issues
  ship within the trailing window." With a single range-driven window the same
  copy still reads correctly (it already says "the trailing window"). No change
  needed unless review disagrees.
- **Should `QualityPanel` gain a uniform `*-chart-window` testid for parity?**
  D4 treats the window title as the indicator (allowed by the spec). If review
  prefers a uniform badge testid across all charts for test ergonomics, add
  `productivity-quality-window` carrying the `from – to` string — a minor,
  additive follow-up, not a contract change.
