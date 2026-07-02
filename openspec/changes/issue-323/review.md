# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: formatting
  Evidence: `ChartGroup.tsx` (line 30), `InsightsCharts.tsx` (line 81), and `InsightsCharts.test.tsx` (line 385) each lacked a trailing newline. POSIX text files should end with a newline.
  Verification: `tail -c 1 <file> | xxd` confirms each file now ends with `0a`. `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` (254 files, 3980 passed) pass after the repair.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: cleanup
  Evidence: `InsightsCharts.tsx:70` had both `readonly GroupSpec[]` (explicit type annotation) and `as const` on the `CHART_GROUPS` array. The explicit type annotation overrides `as const`, making it dead code.
  Verification: Removed `as const`. `npm run typecheck -w packages/web` clean. No behavioral change — the type was always `GroupSpec[]`.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/web/src/pages/insights/panels/FtrTrendChart.tsx:96`
  Evidence: The FtrTrendChart window badge class is `inline-flex items-center rounded-md border border-border bg-muted/40 px-2 py-0.5 tabular-nums`, missing the explicit `text-xs text-muted-foreground` that all five other window badges carry. The badge inherits these from the parent `<div className="flex items-center gap-2 text-xs text-muted-foreground">` (line 92), so the visual result is correct — but the inconsistency makes the badge fragile if the parent styling changes, and the pattern diverges from the sibling charts (ThroughputChart, CycleTimeChart, CumulativeFlowChart, StageDurationChart, CostTrendChart all carry `text-xs tabular-nums text-muted-foreground` on the badge span itself).
  SuggestedAction: Add `text-xs text-muted-foreground` to the FtrTrendChart badge className for consistency with the other five badges.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/web/src/pages/insights/ui/InsightsCharts.test.tsx`
  Evidence: The spec test asserts that the six D4 window badges ARE present but does NOT include a negative assertion that `EpicProgressList` (whose data comes from a live `useEpics` snapshot with no endpoint time window) does NOT render a window badge. The design (D3) and `insights-charts` spec both explicitly exempt `EpicProgressList`. If a future change accidentally adds a badge to it, the test would not catch the regression.
  SuggestedAction: Add an assertion like `expect(screen.queryByTestId('productivity-epic-list-window')).not.toBeInTheDocument()` to `InsightsCharts.test.tsx`.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/web/src/pages/insights/ui/InsightsCharts.test.tsx`
  Evidence: The `insights-charts` spec requires that "Server-range badges are hidden when the range is null (empty state), matching the chart's own empty handling." The individual chart colocated tests verify this (e.g. `CumulativeFlowChart.test.tsx:536`, `StageDurationChart.test.tsx:489`, `CostTrendChart.test.tsx:501`), and the design (D4) confirms empty-state hiding is per-component. The `InsightsCharts` integration test only covers the populated path. This is not a gap in correctness (the per-component tests cover it), but the integration spec does not exercise the empty-state badge path explicitly.
  SuggestedAction: Optionally add an empty-state scenario to the `InsightsCharts` spec test that mocks null/undefined data for the server-range charts and asserts their window badges are absent.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: `packages/web/src/pages/insights/ui/InsightsCharts.test.tsx:241`
  Evidence: The `import { InsightsCharts } from './InsightsCharts'` statement is placed at line 241 (after all builder helpers and mock setup), rather than at the top of the file with the other imports. This is unconventional but legal — TypeScript module-level execution order is deterministic, and `vi.mock()` calls are hoisted regardless. No functional issue.
  SuggestedAction: Move the import to the top of the file alongside the other import statements (around line 16) for conventional module structure.
  Status: pre-existing

- [ID: item-7]
  Severity: info
  Scope: `packages/web/src/pages/dashboard/ui/DashboardPage.test.tsx:73-88`
  Evidence: The Dashboard test still mocks chart-related hooks (`useCompletionTrend`, `useCompletionThroughput`, `useApprovalWait`, `useQualityMetrics`, `useDeliveryTime`, `useEpics`) that were used by the now-removed `ProductivityZone`. These mocks may still be needed by surviving Dashboard widgets (PulseZone, DashboardDigestWidget) — the tests pass without errors, so either they are still consumed or the mock file is benign. A deeper audit of Dashboard widget dependencies would confirm.
  SuggestedAction: Audit whether `PulseZone` or `DashboardDigestWidget` consume these hooks; if not, remove the unused mocks.
  Status: pre-existing

<promise>PASS</promise>
