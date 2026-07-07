# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: test-gap
  Evidence: T-001 / `cumulative-flow-metrics` spec requires that `GET /api/projects/{projectRef}/issues/metrics/cumulative-flow` returns HTTP 404, but the deleted `CumulativeFlow_*` specs were not replaced with a negative test. Without a regression test, re-adding the route mapper could go unnoticed.
  SuggestedAction: Add a theory test asserting 404 for both bare and `?range=30d` cumulative-flow paths.
  Verification: Added `CumulativeFlowEndpoint_Removed_ReturnsNotFound` to `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Api/IssueMetricsApiSpecs.cs:33-46`. Ran `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~IssueMetricsApiSpecs" -p:SkipWebBuild=true` → 59 passed, 0 failed.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: cleanup
  Evidence: `DropStagePopulationSnapshotsTable` Down migration recreated `StagePopulationSnapshots` with columns in the wrong order (`Backlog, Build, Check, Done, Integrate, Plan`) compared to the original `AddStagePopulationSnapshotsTable` Up (`Backlog, Plan, Build, Check, Integrate, Done`). While SQLite tolerates column-order differences, the rollback schema would diverge from the original.
  SuggestedAction: Reorder the Down columns to match the original add migration.
  Verification: Reordered columns in `packages/server/src/Mohist.Server/Infrastructure/Data/Migrations/20260706164309_DropStagePopulationSnapshotsTable.cs`. Ran `dotnet build packages/server/src/Mohist.Server/Mohist.Server.csproj` → 0 warnings, 0 errors; ran `dotnet test ... --filter "FullyQualifiedName~IssueMetricsApiSpecs"` → 59 passed.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: consistency
  Evidence: As noted in the self-review, T-003 (remove `StagePopulationSnapshotService` writer + table + hosting) is backed only by design D4; no `cumulative-flow-metrics` spec scenario asserts that the writer/table are removed. The behavior is correct and the migration is present, but the closest spec still only describes the read-surface removal.
  SuggestedAction: Optionally add one scenario to `specs/cumulative-flow-metrics/spec.md` pinning that no `StagePopulationSnapshotService` writer or `StagePopulationSnapshots` table remains after the read surface is removed, so T-003 has an exact spec anchor.
  Status: follow-up

## Pre-existing or Out-of-scope Items

None.

## Acceptance Criteria Verification

| Criterion | Evidence | Status |
|---|---|---|
| Insights page no longer contains Cumulative Flow chart or empty-state placeholder | `packages/web/src/pages/insights/ui/InsightsCharts.tsx:21-65` renders only `ThroughputChart`, `CompletionTrend`, `CycleTimeChart`, `StageDurationChart`, `QualityPanel`, `FtrTrendChart`, `CostTrendChart`; `CumulativeFlowChart.tsx` is deleted; `entities/issue/api/cumulative-flow.ts` is deleted | met |
| Insights page no longer contains Investment card | `InsightsCharts.tsx` does not import or render `InvestmentPanel`; `InvestmentPanel.tsx` + `.test.tsx` are deleted; `InsightsCharts.test.tsx:202-213` and `InsightsPage.test.tsx:101-112` assert the removed testids are absent | met |
| Insights page no longer contains In-progress Epic progress list | `InsightsCharts.tsx` does not import or render `EpicProgressList`; `EpicProgressList.tsx` + `.test.tsx` are deleted; tests assert the removed testids are absent | met |
| Frontend code + tests for deleted panels cleaned up; dedicated cumulative-flow API hook cleaned up | Deleted: `CumulativeFlowChart.tsx/.test.tsx`, `InvestmentPanel.tsx/.test.tsx`, `EpicProgressList.tsx/.test.tsx`, `cumulative-flow.ts/.test.ts`; removed cumulative-flow exports from `entities/issue/index.ts` | met |
| Cumulative-flow backend endpoint removed with no other consumers | `packages/server/src/Mohist.Server/Api/IssueRoutes.cs:20-25` no longer calls `MapIssueCumulativeFlow()`; `Api/IssueRoutes.CumulativeFlow.cs`, `Issue/Services/CumulativeFlowQuerier.cs` deleted; DTOs removed from `Api/IssueRoutes.Dtos.cs`; new test `IssueMetricsApiSpecs.CumulativeFlowEndpoint_Removed_ReturnsNotFound` verifies 404 for both bare and ranged paths | met |
| Retained charts grouped as 产出 / 交付效率 / 质量 / 投入 in fixed order | `InsightsCharts.tsx:21-65`; `InsightsCharts.test.tsx:182-194` asserts four groups in order `output`, `delivery`, `quality`, `investment`; group-membership tests assert exact panel counts | met |
| `useCostRollup` and `useEpics` hooks and endpoints retained for non-insights consumers | `useCostRollup` still consumed by `packages/web/src/widgets/factory-status/ui/FactoryStatusHeadline.tsx:21`; `useEpics` still consumed by `packages/web/src/pages/epics/ui/EpicListPage.tsx:383`; both endpoints and DTOs remain | met |
| Epics list page progress display unaffected | No changes to `pages/epics/` source; `EpicListPage.tsx` and its tests remain intact | met |
| StagePopulation snapshot writer + table removed | `Events/Hosting/StagePopulationSnapshotService.cs`, `Infrastructure/Data/StagePopulation/StagePopulationSnapshotRow.cs`, `Specs/Events/StagePopulationSnapshotServiceSpecs.cs` deleted; `MohistServiceRegistration.cs` no longer registers the hosted service/options; `MohistDbContext.cs` no longer has the `DbSet`/entity config; `MohistIntegrationFixture.cs` no longer creates the table; `DropStagePopulationSnapshotsTable` migration drops the table and index; `IssueStageAttribution` core retained and used by `IssueMetricsQuerier.cs:883-1639` | met |

## Test Summary

- `npm run typecheck -w packages/web` → passed
- `npm run test:run -w packages/web` → 289 files, 4308 passed, 1 skipped
- `dotnet test ... --filter "FullyQualifiedName~IssueMetricsApiSpecs" -p:SkipWebBuild=true` → 59 passed, 0 failed
- `npm test` (full solution + workspaces) → server: 4013 passed, 12 skipped; web: 4308 passed, 1 skipped; runner: 1028 passed

<promise>PASS</promise>
