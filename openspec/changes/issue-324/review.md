# Review Report

## Result: FAIL

## Repaired Items

_None._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/web/src/pages/insights/panels/QualityPanel.tsx:133-137
  Evidence: The `QualityPanel` renders static labels `title="Last 7 days"` and `title="Last 30 days"` regardless of the active range. When the selector is set to `90d`, the `window30d` DTO slot (driven by the range per design D3) actually spans 90 days of data, but the UI label still reads "Last 30 days". This is misleading to the operator — the label no longer describes the data being shown. The spec requires "Per-chart window badges reflect the selected range" (insights-time-range spec: line 68-69). The `QualityWindow` panel does not derive its title from the actual window data at all.
  SuggestedAction: Derive the `QualityWindow` title from the actual `window.from`/`window.to` fields (or pass the range to derive a label like "Last 7 days" / "Last 30 days" / "Last 90 days" dynamically), matching how `StageDurationChart` and `FtrTrendChart` already derive their window labels from response data.
  Verification: Render `QualityPanel` with `range="90d"`, verify the primary window label reflects the 90-day span (not the hard-coded "Last 30 days").
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: packages/web/src/pages/insights/panels/CumulativeFlowChart.tsx:153, packages/web/src/pages/insights/panels/StageDurationChart.tsx:89, packages/web/src/pages/insights/panels/CostTrendChart.tsx:76
  Evidence: Three panel components destructure the `range` prop with a rename to `_range`: `{ range: _range }`. The leading underscore prefix is a well-established TypeScript convention for *unused* variables (and is often enforced by linters as `no-unused-vars: ["error", { "argsIgnorePattern": "^_" }]`). However, `_range` IS actually used — it is passed to the respective hook (e.g. `useCumulativeFlow(_range)`). This violates the common convention and will confuse maintainers who expect `_`-prefixed identifiers to be intentionally unused.
  SuggestedAction: Use the parameter name `range` directly instead of renaming. If there is a naming conflict with a local variable, rename the local variable instead, or use a meaningful name like `selectedRange`.
  Verification: Grep for `range: _range` — none should remain.
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: packages/web/src/entities/*/api/*.ts (all 7 hook files)
  Evidence: All seven changed hooks import `InsightsRange` from `../../../pages/insights/model/insights-range`. This creates an inverse dependency — the `entities` layer depends on the `pages` layer, which is architecturally inverted (entities should not know about pages). This was likely done to reuse the same type across the codebase, but the `InsightsRange` type (`'7d' | '30d' | '90d'`) is a domain type, not a page concern.
  SuggestedAction: Move `InsightsRange` to a shared location (e.g. `entities/shared/types` or `shared/api`) so both `entities` and `pages` can import it without layering violations. This is non-blocking because it does not cause a runtime bug, but it is an architectural smell worth addressing.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Api/ (6 route files + AgentRoutes.cs)
  Evidence: The same 6-line range-parsing pattern (`string.IsNullOrWhiteSpace(range)` → `MetricsRange.TryParse` → `ApiResults.BadRequest`) is duplicated verbatim across all seven route files. This is copy-paste coupling with a non-trivial validation surface — any change to the validation logic would require touching all seven files. The body and error code string are also identical.
  SuggestedAction: Extract a helper method on the route group (e.g. `TryParseRangeParameter(string? range, out int? windowDays, out IResult? errorResult)`) that encapsulates the parse + 400 path, then call it from each route with a one-liner. This reduces the blast radius of future changes to the range vocabulary.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: packages/web/src/entities/issue/api/completion-trend.ts (vs other hook files)
  Evidence: `useCompletionTrend` and `useCompletionThroughput` construct their `queryKey` with an inline ternary (`range ? [...] : [...]`), while all other hooks (`useDeliveryTime`, `useQualityMetrics`, `useStageDuration`, `useCumulativeFlow`, `useAgentUsage`, `useCostRollup`) use a separately exported `*QueryKey` helper function. The two approaches produce correct keys but are inconsistent across the codebase and harder to test in isolation (the inline ternary requires invoking the hook, while the exported helper can be tested directly).
  SuggestedAction: Unify on the exported-helper pattern used by the six other hooks, either in this change or a follow-up cleanup.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs:1012
  Evidence: `GetCostWindowedAsync` loads ALL project sessions (`ListByLabelsAsync` without `from`/`to` time-range parameters, line 1012) and then filters by `CreatedAt` in memory (line 1033-1044). For projects with large session histories, this pulls unnecessary rows. By contrast, `GetUsageTimeseriesAsync` (line 789-794) correctly passes `from`/`to` to limit the query scope. The cost-windowed method was not updated to use time-range filtering during M3 even though it now accepts a variable `windowDays` parameter.
  SuggestedAction: Add `from`/`to` parameters to the `ListByLabelsAsync` call in `GetCostWindowedAsync` so the DB query is bounded to the window's time range. This is a pre-existing performance issue that was not addressed in this change.
  Status: pre-existing

- [ID: item-7]
  Severity: info
  Scope: packages/server/src/Mohist.Server/Api/IssueRoutes.ApprovalMetrics.cs:39
  Evidence: The approval-wait route continues to use `DateTimeOffset.UtcNow` as its clock source (line 39), rather than an injected `TimeProvider`. This is documented in design.md (line 27-28) as a pre-existing wart (issue-295) that is not in scope for M3. The range parameterization is correctly applied; this note is for traceability only.
  Status: pre-existing

<promise>FAIL</promise>
