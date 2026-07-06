## Why

The Insights page today layers a textual "Signal Summary" on top of its charts, but the two disagree: the summary asserts precise figures (e.g. "average cycle time 2.2h") computed from as few as n=3 samples — most historical issues lack a work-started event so cycle-time is null — while the scatter plot below shows a different caliber in days. Two further caliber mismatches erode trust in every number on the page: the "Cycle Time" card opens on a lead-time lens, and the AI Quality card labels a range-sized window "Last 7 days" while a field named `window30d` silently holds whatever the range selector chose. The fix is to let the charts speak for themselves with honest sample sizes and labels that match their data, and to make the page-level range selector the single source of window truth.

## What Changes

- Remove the "Signal Summary" block (its header, the four verdict cards, and the page subtitle line "先看结论，再看图表") from `/insights`. The chart region directly follows the time-range selector.
- Delete the verdict-derivation model layer (`deriveSignalSummary`, the four per-dimension verdict derivations, the `Verdict` union and helpers) and the `SignalSummary` UI component. The five page-level data hook *calls* in `InsightsPage` (which existed only to feed Signal Summary) are removed; the hook *functions* themselves (`useCompletionThroughput`, `useDeliveryTime`, `useQualityMetrics`, `useCostRollup`, `useStageDuration`) are preserved because the charts still call them.
- Align the delivery-time scatter card's title with its default lens: the title corresponds to whichever lens (lead vs cycle) is shown by default, and switching the lens updates the title or subtitle so the caliber is readable from the title alone. The default lens choice is settled in design.md.
- Convert the AI Quality card from a fixed dual-window layout ("Last 7 days" + a range-sized window) to a single window that follows the page time range. The window title shows the window's actual date span; the hardcoded "Last 7 days" label is removed.
- **BREAKING (HTTP contract, `GET /issues/metrics/quality`)**: the response changes from `{ window7d, window30d, previousFirstTimeRightRate, previousSampleCount, trend }` to a single range-driven primary window whose field name reflects its actual semantics (no more `window30d` holding range-sized data). The aggregation algorithm is unchanged; `trend` and the previous-window comparison continue to scale with the range.
- Hold a cross-cutting floor: every retained chart's data window matches the selected range (verifiable via each chart's date-range label or caption), and every retained chart enters its empty state on 0 or sparse samples — never rendering a precise value without sample-size context. Charts that already do this (Stage Duration, Throughput) keep their behavior.

## Capabilities

- `insights-page-composition`: The `/insights` page renders no Signal Summary block; the chart region directly follows the time-range selector. The verdict-derivation model layer and the `SignalSummary` UI component are removed. The five page-level data hooks remain available (the charts still call them); only the Signal Summary is removed from their consumer list.
- `insights-delivery-time-chart`: The delivery-time scatter card's title is consistent with its default lens, and a lens switch updates the title or subtitle so the metric caliber is readable from the title alone. Covers the title-vs-default-lens alignment and the lens-toggle presentation contract.
- `insights-quality-window`: The AI Quality card renders a single window that follows the page time range, with the window title showing the actual date span. Covers both the frontend single-window layout AND the `GET /issues/metrics/quality` response-structure change from dual `window7d`/`window30d` to a single range-driven window whose field naming matches its actual caliber.
- `insights-chart-presentation`: Cross-cutting presentation floor for every retained chart — the data window matches the selected page range (verifiable via date-range labels/captions), and the chart enters its empty state on 0 or sparse samples without rendering a precise value lacking sample-size context.

## Impact

- **packages/web**:
  - `pages/insights/ui/InsightsPage.tsx` — drop the `insights-signal-section`, the `SignalSummary` import/usage, the now-unused five page-level hook calls (`completion`/`deliveryTime`/`quality`/`cost`/`stageDuration` locals), and the "先看结论" subtitle; keep `<InsightsRangeSelector>` + `<InsightsCharts range>`.
  - `pages/insights/ui/SignalSummary.tsx` — delete.
  - `pages/insights/model/{index,throughput,delivery,quality,investment,verdict}.ts` — delete the verdict-derivation layer; retain `insights-range.ts` and any pure formatting helpers (e.g. `formatCycleDays`) still used by the charts, relocating if needed.
  - `pages/insights/panels/CycleTimeChart.tsx` — make the title track the active lens (default included); update on lens switch.
  - `pages/insights/panels/QualityPanel.tsx` — collapse to a single range-driven window; drop the hardcoded "Last 7 days" label and the second `QualityWindow`; render the window's actual date span.
  - `entities/issue/api/quality-metrics.ts` — update `QualityMetricsResponse` to the single-window DTO shape.
  - Web unit/spec tests for InsightsPage composition, CycleTimeChart title/lens, and QualityPanel windows; remove SignalSummary tests.
- **packages/server**:
  - `Api/IssueRoutes.QualityMetrics.cs` + `Api/IssueRoutes.Dtos.cs` — emit the single-window response; drop `Window7d`/`Window30d`.
  - `Issue/Services/IssueMetricsQuerier.cs` (`GetQualityAsync`) — return one range-driven window; remove the fixed 7-day lens. Aggregation formula unchanged.
  - `Specs/Issue/Api/IssueMetricsApiSpecs.cs` — rewrite `RangeQuery_QualityEndpoint_Window7dStaysFixedAcrossRanges` and `RangeQuery_QualityEndpoint_90dScalesPrimaryPreviousAndTrend` for the single-window contract.
- **No runner / CLI / database / schema changes.** No new dependencies.
- **Risk** (medium): the quality DTO change is a breaking HTTP contract that must land coordinated across server and web; the rest is presentation-layer removal/realignment with no metric-algorithm change. The Signal Summary removal deletes a model layer that is page-internal (no external consumer expected), but call-sites of `deriveSignalSummary`/`Verdict` must be confirmed before deletion.
- Verification: `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`, and `npm test` (server) must pass.
