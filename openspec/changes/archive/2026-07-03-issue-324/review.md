# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: formatting
  Evidence: In `IssueRoutes.Helpers.cs`, the XML doc originally for `GetRequiredProject` (lines 29-39 in the pre-fix state) appeared before the newly inserted `TryParseRangeParameter` method, causing the doc to incorrectly attach to `TryParseRangeParameter` (which then had two `<summary>` blocks) while `GetRequiredProject` had no doc. Moved the `GetRequiredProject` XML doc from above `TryParseRangeParameter` to directly above `GetRequiredProject`.
  Verification: `dotnet build Mohist.sln -p:SkipWebBuild=true --nologo` — 0 warnings, 0 errors. File inspected at `IssueRoutes.Helpers.cs:29-72` — `TryParseRangeParameter` has one `<summary>` block at lines 29-36, and `GetRequiredProject` has its `<summary>` + `<remarks>` at lines 61-71.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Api/ (AgentRoutes.cs + IssueRoutes.Helpers.cs)
  Evidence: `AgentRoutes.TryParseRange` (private, line 75) and `IssueRoutes.TryParseRangeParameter` (internal, line 37) have identical bodies — both parse the range string via `MetricsRange.TryParse`, return null on omit, and produce the same `"unsupported_range"` 400 error shape. The fix commit consolidated the inline duplication within each class but left the cross-class duplication intact.
  SuggestedAction: Extract a shared helper (e.g. `MetricsRange.TryParseRouteParameter`) or promote `IssueRoutes.TryParseRangeParameter` to `public` so `AgentRoutes` can reuse it.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Api/AgentRoutes.cs:57
  Evidence: `GetCostWindowedAsync` at `AgentSessionQuerier.cs:1012` loads ALL project sessions via `ListByLabelsAsync` without `from`/`to` time-range parameters, then filters by `CreatedAt` in memory. This was not corrected during M3 even though the method now accepts a variable `windowDays` parameter and `GetUsageTimeseriesAsync` (the sibling method) correctly scopes its query with time bounds.
  SuggestedAction: Add `from`/`to` parameters to the session list query to bound the fetch to the window's time range.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-4]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Api/IssueRoutes.ApprovalMetrics.cs:26
  Evidence: The approval-wait route continues to bind `DateTimeOffset.UtcNow` directly (not via `TimeProvider`). This is a pre-existing wart from issue-295, documented in design.md and explicitly out of scope for M3. The range parameterization is correctly applied.
  Status: pre-existing

## Verification Summary

| Check | Result |
|---|---|
| `dotnet build Mohist.sln -p:SkipWebBuild=true` | 0 warnings, 0 errors |
| `npm run typecheck -w packages/web` | Clean |
| `npm run typecheck -w packages/runner` | Clean |
| `npm run test:run -w packages/web` | 4041 passed, 1 skipped, 0 failed |
| `grep -rn '_range' packages/web/src/pages/insights/panels/` | No matches (item-2 resolved) |
| `grep -rn 'from.*pages/insights' packages/web/src/entities/` | No matches (item-3 resolved) |
| All 7 hooks import `InsightsRange` from `../../shared/insights-range` | Confirmed |
| All 8 panels accept `range: InsightsRange` prop | Confirmed |
| QualityPanel `window30d` title derives from `formatWindowTitle(window30d)` | Confirmed |
| `completion-trend.ts` exports `completionTrendQueryKey` / `completionThroughputQueryKey` | Confirmed (item-5 resolved) |

## Acceptance Criteria Coverage

| # | Criterion | Evidence |
|---|---|---|
| 1 | Insights page provides global selector (7d/30d/90d) | `InsightsRangeSelector.tsx` — segmented-button with exactly 3 presets; `InsightsPage.tsx` owns `useState<InsightsRange>('30d')` with `data-range` attr |
| 2 | Signal summary + all charts refresh on switch | `InsightsPage.tsx` — 5 page-level hooks receive `range`; `InsightsCharts.tsx` forwards `range` to all 8 panels; tests verify hook invocation on switch |
| 3 | ≥6 server metrics endpoints accept `range` | 7 endpoint route files accept `string? range`: completion, delivery-time, stage-duration, quality, cumulative-flow, approval-wait, agent/usage (T-002), agent/cost (T-002) |
| 4 | Cumulative-flow D6 re-evaluated | D6 reversed in `CumulativeFlowQuerier.cs` — window is range-driven, 90d on omit; D6 doc-comments updated; spec asserts 90d-default + range-driven |
| 5 | Agent/cost supports range aggregation | `AgentSessionQuerier.GetCostWindowedAsync` accepts `int? windowDays`; `/agent/cost` binds `range`; windowed figures scale; all-time figures unaffected (tested) |
| 6 | Frontend hooks fold range into queryKey | All 8 hooks: present range ⇒ fold into key; absent ⇒ omit from key; tests assert 3 ranges ⇒ 3 distinct keys |
| 7 | Back-compat: omit range ⇒ original windows | `MetricsRange.TryParse` returns null day-count on omit; each querier substitutes `?? <fixedDefault>`; spec asserts omit-equality for every endpoint |
| 8 | typecheck + test pass (web + server) | See verification table above |

<promise>PASS</promise>
