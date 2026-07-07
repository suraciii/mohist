## Context

The `/insights` page mixes three components that carry no decision signal with the charts that do: a permanently-empty Cumulative Flow chart, an Investment card whose expanded content duplicates Cost Trend, and an In-progress Epic list that duplicates the Epics page. The prerequisite #389 (chart self-expression + time-range generalization) is done, so every retained chart already stands on its own; this change is pure subtraction and reordering on top of that stable baseline.

The page is composed declaratively in `InsightsCharts.tsx` via a single `CHART_GROUPS` array (one entry per dimension: 产出 / 交付效率 / 质量 / 投入). Each entry's `render` fn lists its panels in order. This makes subtraction (drop panels) and reordering (drop from the wrong group, keep group skeleton) a localized edit to that one array plus deletion of the orphaned panel files.

The proposal defers one question to this doc: the disposition of `StagePopulationSnapshotService` and its `StagePopulationSnapshots` table once the cumulative-flow read surface is gone. A full dependency walk settles it:

- **Only reader** of `StagePopulationSnapshots` is `CumulativeFlowQuerier.GetAsync` (`CumulativeFlowQuerier.cs:79`), which is being deleted. No other code path queries the table.
- **Writer** is `StagePopulationSnapshotService`, a `BackgroundService` registered in `MohistServiceRegistration.cs:86-90` that upserts one row per project per UTC day.
- **Shared attribution core** `IssueStageAttribution` is **not** exclusive to the writer — `IssueMetricsQuerier` (the retained stage-duration surface) also calls `IssueStageAttribution.Attribute`. So removing the writer does not entitle removing the attribution core.

Constraints: the change is labeled `risk=low`, `effort=small`. Backend is C# with `TreatWarningsAsErrors` acting as lint. Tests run on SQLite via a manual-schema integration fixture (`MohistIntegrationFixture`), which currently issues raw `CREATE TABLE IF NOT EXISTS "StagePopulationSnapshots" ...` (`MohistIntegrationFixture.cs:248-260`) because the fixture does not run EF migrations.

## Goals / Non-Goals

**Goals:**
- End-to-end removal of the cumulative-flow read surface: HTTP route, querier, response DTOs, frontend hook/DTO types, panel component, and all their tests.
- Removal of the Investment panel and the In-progress Epic progress list (panel + tests), **without** disturbing their shared data hooks' other consumers.
- Reorder the retained charts into the fixed four-dimension layout (产出 → 交付效率 → 质量 → 投入) with the exact chart membership mandated by `insights-page-composition`.
- Settle the writer disposition: remove the now-reader-less `StagePopulationSnapshotService` write pipeline and its table, keeping the shared `IssueStageAttribution` core intact.

**Non-Goals:**
- Touch any retained chart's internal expression, calibration, or naming (that was #389, done).
- Backfill Cumulative Flow history. The chart is deleted, not rescued.
- Add anything new, change the range selector, or alter the Epics list page.
- Remove `useCostRollup` / `useEpics` hooks or their endpoints — they stay for `FactoryStatusHeadline` and `EpicListPage`.

## Decisions

### D1 — Remove the cumulative-flow read surface end-to-end (BREAKING HTTP)
Delete `Api/IssueRoutes.CumulativeFlow.cs`, drop the `MapIssueCumulativeFlow()` call in `Api/IssueRoutes.cs:25`, delete `Issue/Services/CumulativeFlowQuerier.cs`, delete the `CumulativeFlowResponse` / `CumulativeFlowDayDto` records from `Api/IssueRoutes.Dtos.cs`, and delete the `CumulativeFlow_*` specs from `IssueMetricsApiSpecs.cs` (plus the `SeedStagePopulationSnapshotAsync` helper once nothing in that file seeds snapshots).

On the web side: delete `entities/issue/api/cumulative-flow.ts` + `.test.ts`, remove the `useCumulativeFlow` / `fetchCumulativeFlow` / `cumulativeFlowQueryKey` / `CumulativeFlowResponse` / `CumulativeFlowDayDto` exports from `entities/issue/index.ts`, and delete `pages/insights/panels/CumulativeFlowChart.tsx` + `.test.tsx`.

**Rationale:** the spec pins "the route is gone, not merely unrendered." A half-removal (unrendered client + live endpoint) would leave a breaking-contract-looking endpoint serving a dead surface. **Alternative considered:** keep the endpoint and only unrender the client — rejected, violates the `cumulative-flow-metrics` requirement and leaves a zombie route.

The breaking surface is internal: the sole consumer is the hook deleted in the same change.

### D2 — Remove Investment panel + Epic progress list, retain their shared hooks
Delete `pages/insights/panels/InvestmentPanel.tsx` + `.test.tsx` and `pages/insights/panels/EpicProgressList.tsx` + `.test.tsx`. **Do not** touch `useCostRollup` or `useEpics` — `FactoryStatusHeadline` and `EpicListPage` still consume them (`insights-page-composition` spec scenario: "Shared data hooks MUST remain available").

**Rationale:** the panels added no metric dimension (Investment = Cost Trend re-sliced; Epic list = Epics-page status re-stated). The hooks and endpoints are not panel-local and so stay. **Alternative considered:** rip out `useCostRollup` too — rejected by explicit dependency check, it has a live non-insights consumer.

### D3 — Reorder via the existing `CHART_GROUPS` skeleton
Edit only the `render` fns in `InsightsCharts.tsx`: drop `EpicProgressList` and `CumulativeFlowChart` from the 产出 group (leaving Throughput + Completion Trend), drop `InvestmentPanel` from the 投入 group (leaving Cost Trend). The 交付效率 and 质量 groups are already correctly composed and untouched. Group titles, questions, ids, and ordering are unchanged — the four-group skeleton is a stable asset, not something this change redesigns.

Update `InsightsCharts.test.tsx` and `InsightsPage.test.tsx` to drop mocks/assertions for the removed panels and the `useCumulativeFlow` mock, keeping assertions for the four-group order and retained-chart membership.

**Rationale:** the skeleton already encodes the target dimension model from #389, so reordering is subtraction inside `render`, not restructuring.

### D4 — Remove the snapshot writer + table + hosting (settles the deferred question)
Delete `Events/Hosting/StagePopulationSnapshotService.cs` (including `StagePopulationSnapshotOptions` and `StagePopulationSnapshotCounts`), remove the `AddOptions<StagePopulationSnapshotOptions>()` + `AddHostedService<StagePopulationSnapshotService>()` block in `MohistServiceRegistration.cs:86-90`, delete the `DbSet<StagePopulationSnapshotRow>` mapping + entity config from `MohistDbContext.cs` (lines 71, 722-733), delete `Infrastructure/Data/StagePopulation/StagePopulationSnapshotRow.cs`, delete `tests/.../Specs/Events/StagePopulationSnapshotServiceSpecs.cs`, remove the snapshot-table creation block from `MohistIntegrationFixture.cs:242-260`, and add an EF migration `DropStagePopulationSnapshotsTable` that drops the table + its `UQ_StagePopulationSnapshots_ProjectId_Day` index.

**Rationale:** after D1 there are zero readers. Leaving the writer means a daily `BackgroundService` upserting one row per project forever into a table nothing reads — active cost (DB writes, hosting cycle) plus passive debt (future readers must investigate whether the data is meaningful). The `AGENTS.md` principle "数据模型应该尽可能地简洁" cuts toward removal. The blast radius is fully contained inside the `Events/Hosting/` slice plus a mechanical migration.

**Keep:** `IssueStageAttribution` (the attribution core) — it is shared with the retained stage-duration surface in `IssueMetricsQuerier`. Removing the writer removes only the snapshot materialization, not the attribution logic.

**Alternative considered:** defer writer removal to a follow-up issue to keep this change maximally minimal. Rejected: splitting one logical cut ("cumulative-flow is gone") across two issues leaves a write pipeline with no reader in the interim, and the migration risk does not decrease by waiting — the table has the same shape whenever it is dropped.

### D5 — Backend endpoint removal is a hard delete, not a deprecation
No 410 Gone, no redirect, no aliasing. The `cumulative-flow-metrics` spec scenario requires a request to the old path to fail as an unmatched route (HTTP 404). Re-routing would re-break the contract the spec pins.

## Risks / Trade-offs

- **[Breaking HTTP contract on the `cumulative-flow` route]** → Sole consumer (`useCumulativeFlow`) is deleted in the same change; the dependency grep confirms no other client. Documented in the PR body per the AC.
- **[Writer removal pulls a schema migration into a `risk=low` issue]** → The migration is purely subtractive (drop table + index) and reversible via EF's auto-generated `Down`. Local-first single-user deployment means no coordinated rollout. The deleted rows had no reader, so no data of value is lost.
- **[Integration fixture drift]** → The fixture manages schema via raw SQL, not EF migrations. The fixture's `CREATE TABLE IF NOT EXISTS "StagePopulationSnapshots"` block and the EF entity config must be removed in the same change; otherwise typecheck/build stays green but the fixture keeps creating an orphan table. Mitigation: delete the fixture block in the same commit as the `DbSet` removal.
- **[Accidentally removing shared code]** → `IssueStageAttribution` is consumed by the retained `IssueMetricsQuerier`. The deletion scope is the snapshot writer + row + table only; the attribution core stays. Verified by grep.
- **[Test regressions in retained metrics specs]** → `IssueMetricsApiSpecs.cs` shares the file with retained metrics-endpoint specs. Only the `CumulativeFlow_*` specs and the now-orphaned `SeedStagePopulationSnapshotAsync` helper are deleted; the other specs are untouched.

## Migration Plan

Single PR, single deploy. Edit order (each step keeps the tree compiling within its package):

1. **Web deletions + reorder:** delete the three panel files + their tests, delete `cumulative-flow.ts` + test, prune `entities/issue/index.ts` exports, edit `CHART_GROUPS` render fns in `InsightsCharts.tsx`, update `InsightsCharts.test.tsx` + `InsightsPage.test.tsx`.
2. **Backend read-surface deletion:** delete `IssueRoutes.CumulativeFlow.cs`, drop the registration line, delete `CumulativeFlowQuerier.cs`, prune DTOs from `IssueRoutes.Dtos.cs`, prune `CumulativeFlow_*` specs + the orphan seed helper.
3. **Backend writer + table removal:** delete `StagePopulationSnapshotService.cs`, `StagePopulationSnapshotRow.cs`, `StagePopulationSnapshotServiceSpecs.cs`; prune `MohistServiceRegistration.cs`, `MohistDbContext.cs`, `MohistIntegrationFixture.cs`; add the drop migration.
4. **Verify:** `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`, `npm test` (server).

**Rollback:** revert the PR. The drop migration's `Down` re-creates the table; or, because the deleted snapshots had no consumer, restoring the pre-migration DB snapshot is equally acceptable for a local-first single-user system. No data backfill is required on rollback or forward.

## Open Questions

None blocking. The one question the proposal deferred (writer + table disposition) is settled by D4. If a future cumulative-flow-like surface is wanted, it re-introduces its own writer under its own issue; the shared `IssueStageAttribution` core is preserved here precisely to make that possible.
