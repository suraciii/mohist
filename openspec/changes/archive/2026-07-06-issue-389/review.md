# Review Report

## Result: PASS

## Repaired Items

None.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Api/IssueMetricsApiSpecs.cs` lines 443, 480, 500, 579
  Evidence: Test names still use plural window wording after the single-window contract change (`ReturnsBothWindowsWithRates`, `ReturnsEmptyResultPerWindow`, `ReturnsTrendAlongsideWindows`, `BothWindowsReturned_DeltaDerivableAcrossAdjacent30DayWindows`). The assertions are correct, but the names now mislead future readers.
  SuggestedAction: Rename the tests to single-window language, e.g. `QualityMetrics_ShippedIssuesWithRepairs_ReturnsWindowWithRates`, `QualityMetrics_NoShippedIssues_ReturnsEmptyWindow`, `QualityMetrics_ShippedIssuesWithRepairs_ReturnsTrendAlongsideWindow`, `QualityMetrics_CurrentAndPreviousWindows_DeltaDerivableAcrossAdjacent30DayWindows`.
  Verification: `dotnet test Mohist.Server.Tests --filter "FullyQualifiedName~QualityMetrics"` still passes.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/web/src/pages/insights/ui/InsightsCharts.test.tsx` lines 453–576
  Evidence: The cross-chart range-reflection test asserts window badges for throughput, cycle time, stage duration, cumulative flow, FTR trend and cost trend, but does not assert the AI Quality panel's `from – to` title (`productivity-quality-window`). The QualityPanel unit test covers the title, so this is a coverage gap rather than a behavior gap.
  SuggestedAction: Add an assertion such as `expect(screen.getByTestId('productivity-quality-window').querySelector('h4')?.textContent).toContain('Jun 23')` in the 7d branch and a corresponding 90d assertion.
  Verification: `npm run test:run -w packages/web` still passes.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueMetricsQuerier.cs` lines 464–475
  Evidence: The quality trend is bucketed by calendar days (`today.AddDays(-(primaryDays - 1))` to `today`), while the primary window is anchored on `now` (`now.AddDays(-primaryDays)` to `now`). When `now` is not at a UTC midnight boundary, the trend's first/last calendar day can drift from the window endpoints even though `Trend.From/To` are copied from `Window.From/To`. This is pre-existing aggregation behavior and the issue explicitly preserves algorithms, but a strict reading of `insights-quality-window` ("trend span equals the primary window span") may be surprised by the partial-day mismatch.
  SuggestedAction: Document the calendar-day semantics in the DTO/XML docs, or if exact span parity is required, bucket by 24-hour offsets anchored on `now`.
  Verification: Add a server spec that pins `now` at a non-midnight offset and asserts the first/last trend bucket boundaries.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/web/src/pages/insights/panels/StageDurationChart.tsx` lines 112–119, `CumulativeFlowChart.tsx` lines 170–177, `CostTrendChart.tsx` lines 90–97, `FtrTrendChart.tsx` lines 94–101
  Evidence: These charts only render their window badge when data is present. In the empty state a user cannot see which range produced the empty result. `ThroughputChart.tsx` already shows the badge unconditionally and is the more consistent pattern.
  SuggestedAction: Render a range caption (e.g. the selected range code or the response window) in the empty state for the retained charts.
  Verification: Add empty-state assertions for each chart's window indicator and run `npm run test:run -w packages/web`.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: `packages/web/src/pages/insights/panels/InvestmentPanel.tsx`, `packages/web/src/pages/insights/panels/EpicProgressList.tsx`
  Evidence: `InvestmentPanel` shows a "Window / Population" basis of "per-project agent/session usage, cumulative across project history" rather than the selected date range, and `EpicProgressList` does not consume `range` at all. `design.md` D4 and `self-review.md` explicitly scope these two panels out as companion-issue work.
  SuggestedAction: Address in the companion chart-trimming issue, and optionally add a one-line scope note to `specs/insights-chart-presentation/spec.md` that the floor applies to time-windowed metric charts only.
  Status: out-of-scope

- [ID: item-6]
  Severity: info
  Scope: `packages/web/src/pages/insights/panels/QualityPanel.tsx` lines 13–22
  Evidence: `formatWindowTitle` parses the ISO date portion and constructs a local-midnight `Date`, ignoring the timezone offset. In non-UTC client locales this can shift the displayed day. The same pattern is used by other chart formatters, so it is not introduced by this change.
  SuggestedAction: If locale-correct day labels become a requirement, migrate all Insights date formatters to a shared, offset-aware helper.
  Status: pre-existing

## Acceptance Criteria Verification

| # | Criterion | Evidence | Status |
|---|-----------|----------|--------|
| 1 | Insights page no longer contains the Signal Summary block; charts follow the range selector directly. | `packages/web/src/pages/insights/ui/InsightsPage.tsx` no longer imports or renders `SignalSummary`; `InsightsPage.test.tsx:73–95` asserts `insights-signal-section` and `signal-summary` are absent; `App.test.tsx:189–200` confirms the same at the routing level. | Met |
| 2 | Signal Summary UI component and verdict model layer removed; page-level hooks preserved. | `SignalSummary.tsx` and verdict model files are deleted; `packages/web/src/pages/insights/index.ts` only exports `InsightsPage` and `insights-range`; `InsightsCharts.tsx` still calls `useCompletionThroughput`, `useDeliveryTime`, `useQualityMetrics`, `useStageDuration`, etc. | Met |
| 3 | Delivery-time scatter card title matches default lens and updates on lens switch. | `CycleTimeChart.tsx:82–103` defaults `lens` to `'lead'` and renders `lens === 'lead' ? 'Lead Time' : 'Cycle Time'`; `CycleTimeChart.test.tsx:388–424` asserts default title, cycle switch, and lead restore. | Met |
| 4 | AI Quality card renders a single range-driven window whose title shows the actual date span. | `QualityPanel.tsx:93–131` renders exactly one `QualityWindow` from `data.window` with `formatWindowTitle(window)`; `QualityPanel.test.tsx:77–126` asserts a single window block and the formatted `from – to` title. | Met |
| 5 | No hardcoded "Last 7 days" label; backend response field naming matches actual caliber. | `QualityPanel.tsx` no longer contains "Last 7 days"; `IssueRoutes.Dtos.cs:311–315` defines `QualityMetricsResponse(Window, PreviousFirstTimeRightRate, PreviousSampleCount, Trend)` with no `Window7d`/`Window30d`; `IssueRoutes.QualityMetrics.cs:34–39` maps the single window. | Met |
| 6 | 7d/30d/90d ranges are reflected in all retained chart windows. | `InsightsCharts.test.tsx:380–451` verifies every panel hook is called with the selected range; `ThroughputChart.test.tsx:411–445`, `CycleTimeChart.test.tsx:456–499`, `StageDurationChart`/`CumulativeFlowChart`/`FtrTrendChart`/`CostTrendChart` badges, and `QualityPanel.test.tsx` title all reflect the selected window. | Met |
| 7 | Retained charts enter empty state on zero/sparse samples without showing unsupported precise values. | `QualityPanel.tsx:96–116` empties when `window.sampleCount === 0`; `CycleTimeChart.tsx:86–96` empties when `visibleCount === 0`; `StageDurationChart`, `ThroughputChart`, `FtrTrendChart`, `CumulativeFlowChart`, `CostTrendChart` all gate resolved state on sample presence; corresponding tests assert empty state and absence of fabricated percentages. | Met |

## Verification Commands Run

- `npm run typecheck -w packages/web` — passed.
- `npm run test:run -w packages/web` — 293 test files, 4362 tests passed, 1 skipped.
- `dotnet test Mohist.sln -p:SkipWebBuild=true --no-build` — 4038 passed, 12 skipped, 0 failed.
- `npm test` (root, includes runner workspace suite) — 72 test files, 1028 tests passed.

<promise>PASS</promise>
