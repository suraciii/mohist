# Review Report

## Result: PASS

## Repaired Items

None.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs`, `packages/server/src/Mohist.Server/Api/AgentRoutes.cs`
  Evidence: The rollup intentionally keeps the existing single-currency behavior from the 7-day usage timeseries: `GetCostRollupAsync` sums all `CostAmount` values and keeps the first non-null currency (`AgentSessionQuerier.cs:346-357`), and `BuildCostPerShip` carries that currency into the ratio (`AgentRoutes.cs:64-69`). This is documented as a known trade-off in `openspec/changes/issue-262/design.md:76-81` and is not a regression from the candidate requirements, but it can misrepresent projects that later contain real mixed-currency usage.
  SuggestedAction: Add a per-currency breakdown or reject/segment mixed-currency aggregation once the product needs to support multi-currency projects.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/web/src/pages/dashboard/productivity/InvestmentPanel.tsx`, `packages/web/src/widgets/factory-status/ui/FactoryStatusHeadline.tsx`
  Evidence: Both UI surfaces treat a missing or errored cost-rollup query as the same no-sample state as a successful zero-sample response: `InvestmentPanel` returns empty when `rollup` is undefined (`InvestmentPanel.tsx:21-23`), and the headline renders `-` when `todayCost` is undefined (`FactoryStatusHeadline.tsx:33-36`). This matches nearby dashboard panels that do not distinguish loading/error from empty, and it does not violate the issue acceptance criteria, but it can hide backend/API failures from users.
  SuggestedAction: Consider adding shared dashboard query error/loading affordances if these panels need operational diagnostics rather than silent empty states.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: info
  Scope: `npm test` verification
  Evidence: The first `npm test` invocation hit the 120s tool timeout while server tests were still running, not a test failure. A second run with a 300s timeout completed successfully. The successful run included `dotnet test Mohist.sln -p:SkipWebBuild=true`, web CI tests through the root script, and runner workspace tests; final summary showed runner `48 passed | 3 skipped` files and `662 passed | 23 skipped` tests after the server and web phases completed.
  SuggestedAction: Keep the longer timeout for full-suite review runs in this repository.
  Status: out-of-scope

- [ID: item-4]
  Severity: info
  Scope: `npm run test:run -w packages/web`
  Evidence: Vitest emits `DEPRECATED  test.poolOptions was removed in Vitest 4` during the web test run. The suite still passes (`171 passed`, `2449 passed | 1 skipped`), and this warning is unrelated to the cost-rollup candidate.
  SuggestedAction: Update the Vitest configuration in a separate maintenance change.
  Status: pre-existing

## Acceptance Evidence

- Issue criterion: project-level token/cost aggregation endpoint with trailing 7d plus cumulative. Evidence: existing `/api/projects/{projectRef}/agent/usage` remains mapped at `AgentRoutes.cs:40-44`; new `/api/projects/{projectRef}/agent/cost` is mapped at `AgentRoutes.cs:46-59`; `GetCostRollupAsync` computes cumulative total plus current UTC-day cost from existing session usage at `AgentSessionQuerier.cs:323-363`. Regression evidence: `AgentCostRollupApiSpecs.cs` covers endpoint shape, total summing, skipped no-usage sessions, current-day bucket behavior, unknown project 404, and unchanged `/agent/usage` availability.
- Issue criterion: cumulative spend plus cost-per-merge in `InvestmentPanel`. Evidence: `InvestmentPanel.tsx:27` fetches the rollup via `useCostRollup`; `InvestmentPanel.tsx:94-137` renders cumulative spend, cost-per-ship, and shipped issue count; `InvestmentPanel.tsx:84-92` keeps a defined no-spend empty state rather than the old placeholder shell. Regression evidence: `InvestmentPanel.test.tsx` covers populated rendering, no-spend empty state, undefined cost-per-ship, and real `$0.00` handling.
- Issue criterion: dashboard headline today-cost wiring. Evidence: `FactoryStatusHeadline.tsx:21-25` reads `useCostRollup().data?.todayCost`; `FactoryStatusHeadline.tsx:33-36` preserves empty-vs-zero display; `FactoryStatusHeadline.tsx:74-82` renders the today-cost field. `deriveFactoryStatus` carries the metric through without collapsing `sampleCount` at `factory-status.ts:22-52`. Regression evidence: `FactoryStatusHeadline.test.tsx` and `factory-status.test.ts` cover populated today cost, empty placeholder, genuine zero, and unchanged sibling headline values.
- Issue criterion: aggregation logic tests. Evidence: `AgentCostRollupApiSpecs.cs` covers total/today/cost-per-ship/done-count behavior, independent emptiness, real-zero semantics, unknown project 404, and existing usage endpoint preservation.

## Verification

- `git diff --check master...HEAD` passed.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 171 files, 2449 passing tests, 1 skipped.
- `npm test` passed on the rerun with a 300s timeout. The first 120s invocation timed out while still executing server tests; it was not a test assertion failure.

<promise>PASS</promise>
