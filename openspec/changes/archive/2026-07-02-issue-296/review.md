# Review Report

## Result: FAIL

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: none
  Evidence: No safe local repair was applied. The remaining findings require product behavior and test changes around the public quality-metrics contract, so they are disallowed for repair during review [disallowed:product-behavior-change/public-contract-semantics].
  Verification: Reviewed issue 296, `proposal.md`, `design.md`, both delta specs, `tasks.json`, `self-review.md`, and every changed product/test file in the candidate diff. Ran `npm test` successfully with a 300s timeout after an earlier 120s run timed out during the workspace Vitest phase; also ran `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` successfully.
  Status: resolved

## Blocking Items

- [ID: item-2]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs`
  Evidence: The accepted spec requires the per-bucket FTR/rework series to cover the same trailing window as the existing quality aggregation, and `tasks.json` pins the API response to a 30-element daily array spanning `Window30d.From`..`To`. The implementation sets `window30dFrom = now.AddDays(-30)` and includes scalar samples where `shipTime >= window30dFrom && shipTime <= window30dTo` (`IssueQuerier.cs:532-535`, `IssueQuerier.cs:655-662`), but it builds trend boundaries from `today.AddDays(-29)` through today (`IssueQuerier.cs:537-546`). With `now = 2026-06-19T12:00Z`, a shipped issue at `2026-05-20T13:00Z` is inside `Window30d` but has boundary `2026-05-20`, which is not present in the trend series. The response still reports `Trend.From == Window30d.From` and `Trend.To == Window30d.To` (`IssueRoutes.QualityMetrics.cs:45-56`), so clients receive a trend that claims to span the scalar 30-day window while silently dropping valid first-day samples [disallowed:product-behavior-change/public-contract-semantics].
  SuggestedAction: Make the trend window and the scalar 30-day window use the same temporal semantics. Either bucket the whole `window30dFrom..window30dTo` range so in-window first-day samples appear in the first trend bucket, or explicitly change the trend `From`/`To` and scalar relationship to a calendar-day window if that is the intended product contract. Update API/querier tests to pin the chosen boundary behavior.
  Verification: Add a regression test with `now` at midday and a shipped issue after `now.AddDays(-30)` but before the first emitted calendar boundary; verify both `Window30d.SampleCount` and the corresponding trend bucket include that issue. Then rerun `npm test`, `npm run typecheck -w packages/web`, and `npm run test:run -w packages/web`.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Querier/IssueQuerierSpecs.cs` and `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Api/IssueMetricsApiSpecs.cs`
  Evidence: The new tests verify dense shape and several sampled calendar days, but they do not cover the partial first day of `Window30d`. `GetQualityAsync_Trend_ReturnsPreSizedThirtyDayDailySeries` asserts first boundary `2026-05-21` while also asserting `Trend.Window30dFrom == Window30d.From` (`IssueQuerierSpecs.cs:1713-1719`), and `GetQualityAsync_Trend_IncludesLeadingCalendarBoundarySample` places the leading sample on `2026-05-21T09:00Z` (`IssueQuerierSpecs.cs:1737-1756`). Neither test places a sample in the actual scalar window segment from `2026-05-20T12:00Z` to `2026-05-21T00:00Z`, so the scalar/trend divergence in item-2 remains undetected. The API test mirrors the first emitted boundary rather than the `Window30d.From` boundary (`IssueMetricsApiSpecs.cs:357-407`) [disallowed:test-behavior-change/product-behavior-change].
  SuggestedAction: Add querier and API regression coverage for the first partial-day boundary, including assertions that the trend's advertised `from`/`to` and emitted bucket boundaries cannot contradict the samples counted by `Window30d`.
  Verification: Run the new focused tests and the full verification set: `npm test`, `npm run typecheck -w packages/web`, and `npm run test:run -w packages/web`.
  Status: open

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: Dashboard chart copy/affordance
  Evidence: The rework overlay is initially off and the legend is hidden for the single-series state because `ChartLegend` returns null for one entry. This satisfies the current acceptance criteria, but users may not discover that the line can be compared with rework unless they notice the checkbox.
  SuggestedAction: After the boundary fix, consider whether the overlay should default on when rework data exists or whether the control needs stronger visual treatment consistent with the dashboard controls.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: repository verification
  Evidence: The first `npm test` run with a 120s tool timeout completed the .NET suite successfully but timed out after starting the workspace Vitest phase. A later `npm test` run with a 300s timeout completed successfully. Targeted `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` also completed successfully.
  SuggestedAction: None for this change; use a longer timeout for full monorepo verification in this workspace.
  Status: out-of-scope

<promise>FAIL</promise>
