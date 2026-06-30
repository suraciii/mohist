# Review Report

## Result: PASS

## Repaired Items

None. No safe, local review repairs were made.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/web/src/pages/dashboard/productivity/CostTrendChart.tsx`
  Evidence: The implementation intentionally treats shipped issues with zero recorded usage cost as resolved chart data, because the server now returns `cumulativeCost: 0` and `costPerShip: 0` for shipped-without-usage days (`AgentUsageTimeseriesApiSpecs.cs` covers this at `GetUsage_CumulativeCostPerShipIsZeroWhenShippedExistsButNoUsageSamples`). This satisfies the genuine-zero cost-per-ship requirement, but it leaves a product wording nuance: the cost chart's empty copy still says data appears once an agent session reports usage, while a project with shipped issues and no usage can now show a zero trend instead of that empty state.
  SuggestedAction: Clarify the product copy or spec language in a future chart polish pass so the empty-state definition and genuine-zero trend semantics are explicit for shipped-without-usage projects.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: info
  Scope: `packages/web/src/pages/dashboard/productivity/EpicProgressList.tsx`, `packages/web/src/pages/dashboard/productivity/SnapshotRow.tsx`
  Evidence: Existing non-chart dashboard widgets still contain Tailwind hardcoded color utilities such as `bg-blue-600`, `text-green-600`, `text-red-600`, and `text-blue-600`. The issue and candidate boundary scope the color-token requirement to chart surfaces; chart surfaces in this candidate use token classes such as `fill-chart-*`, `stroke-chart-*`, `stroke-border`, and `fill-muted-foreground`.
  SuggestedAction: Convert remaining non-chart widget status colors to theme/status tokens in a separate dashboard styling cleanup.
  Status: pre-existing

Verification performed on the post-build candidate snapshot:

- `npm run typecheck -w packages/web`: passed.
- `npm run test:run -w packages/web`: passed, 202 files / 3065 tests passed, 1 skipped.
- `npm test`: passed. Server: 3132 passed, 13 skipped. Web: 3065 passed, 1 skipped. Runner/workspaces: 786 passed, 23 skipped.
- `git diff --check origin/master...HEAD`: passed.

Acceptance criteria evidence reviewed:

- Daily cost chart is mounted in Productivity after `InvestmentPanel` in `packages/web/src/pages/dashboard/productivity/ProductivityZone.tsx` and renders one bar per `/agent/usage` bucket in `CostTrendChart.tsx`.
- Cost-per-ship trend values are sourced from the additive server `cumulativeCostPerShip` series, with null points skipped and genuine zero plotted.
- Shared chart baseline exists under `packages/web/src/pages/dashboard/charts/`, with `ChartContainer`, `ChartAccessibility`, `BarSeries`, `LineSeries`, `ChartAxes`, `ChartLegend`, and `useReducedMotion` covered by component tests.
- Server `/api/projects/{projectRef}/agent/usage` keeps the existing bucket contract and adds `CumulativeCostPerShip` in `AgentUsageTimeseriesDto`; API tests cover prefix sums, zero-sample, unknown-project 404, undefined-vs-zero, and pre-window history.

<promise>PASS</promise>
