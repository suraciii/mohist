## Why

The Insights page is supposed to answer "最近做得怎么样、该改什么", but it is diluted by three components that carry no decision signal: a Cumulative Flow chart that is permanently empty (its snapshot writer does not backfill history and landed after the event stream it depends on was itself empty), an Investment card whose expanded content duplicates the Cost Trend chart below it, and an In-progress Epic list that re-states project status already shown on the Epics page. Users must scroll past and mentally filter these every visit. This change converges the page to a signals-only tool: only charts that express a metric, grouped by the four decision dimensions.

## What Changes

- **Remove the Cumulative Flow chart** from `/insights`, including its empty-state placeholder copy. Its sole frontend consumer is the deleted chart, so the full read surface is removed end-to-end: the `GET /api/projects/{id}/issues/metrics/cumulative-flow` endpoint, the `CumulativeFlowQuerier`, its response DTOs, and the frontend `useCumulativeFlow` / `fetchCumulativeFlow` hook. **BREAKING (HTTP contract)** — the `cumulative-flow` route no longer exists. The disposition of the underlying `StagePopulationSnapshotService` writer and its snapshot table (no remaining reader after this change) is settled in design.md.
- **Remove the Investment card** from `/insights`. It adds no metric dimension beyond what Cost Trend already expresses (total spend, cost-per-ship, shipped count are the same data sliced differently). The `useCostRollup` hook and its endpoint are **retained** — `FactoryStatusHeadline` on the dashboard still consumes them.
- **Remove the In-progress Epic progress list** from `/insights`. Epic progress is project status, not a metric insight, and the Epics list page already renders the same source data grouped by status. The `useEpics` hook and its endpoint are **retained** — `EpicListPage` still consumes them.
- **Reorder the retained charts into four dimension groups**, in this order: 产出 (Throughput, Completion Trend) → 交付效率 (Cycle Time, Stage Duration) → 质量 (AI Quality, First-Time-Right Trend) → 投入 (Cost Trend). The 交付效率 group is the action surface — which stage is slowest, where rework concentrates.
- The output group drops from four children to two; the investment group drops from two to one. The four-group skeleton (titles, questions, ordering) and the time-range selector are unchanged.
- No retained chart's internal expression, calibration, or naming is touched (that is the prerequisite issue #389's territory, now done). No new charts. No change to the range selector or the Epics list page.

## Capabilities

- `insights-page-composition`: The `/insights` page renders only signal charts, grouped and ordered by the four decision dimensions (产出 / 交付效率 / 质量 / 投入). The Cumulative Flow chart, the Investment card, and the In-progress Epic progress list MUST NOT render on `/insights`. The shared data hooks (`useCostRollup`, `useEpics`) remain available to their other consumers and are not removed as a side effect of dropping these panels.
- `cumulative-flow-metrics`: The cumulative-flow read surface is removed end-to-end — the HTTP endpoint `GET /issues/metrics/cumulative-flow` no longer exists (and is not re-routed), the `CumulativeFlowQuerier` and its response DTOs are deleted, and the frontend `useCumulativeFlow` / `fetchCumulativeFlow` hook and its DTO types are deleted. This pins the breaking contract: the route is gone, not merely unrendered.

## Impact

- **packages/web**:
  - `pages/insights/ui/InsightsCharts.tsx` — drop imports and rendering of `EpicProgressList`, `CumulativeFlowChart`, `InvestmentPanel`; the output group keeps Throughput + Completion Trend, the investment group keeps only Cost Trend.
  - `pages/insights/panels/CumulativeFlowChart.tsx` + `.test.tsx` — delete.
  - `pages/insights/panels/InvestmentPanel.tsx` + `.test.tsx` — delete.
  - `pages/insights/panels/EpicProgressList.tsx` + `.test.tsx` — delete.
  - `entities/issue/api/cumulative-flow.ts` + `.test.tsx` — delete; remove the `useCumulativeFlow` / `fetchCumulativeFlow` / `cumulativeFlowQueryKey` exports and the `CumulativeFlowResponse` / `CumulativeFlowDayDto` type exports from `entities/issue/index.ts`.
  - `pages/insights/ui/InsightsCharts.test.tsx`, `InsightsPage.test.tsx` — drop mocks/assertions for the removed panels and the cumulative-flow hook; keep assertions for the four-group order and the retained charts.
- **packages/server**:
  - `Api/IssueRoutes.CumulativeFlow.cs` — delete; remove the `MapIssueCumulativeFlow()` call in `Api/IssueRoutes.cs:25`.
  - `Issue/Services/CumulativeFlowQuerier.cs` — delete.
  - `Api/IssueRoutes.Dtos.cs` — delete the `CumulativeFlowResponse` / `CumulativeFlowDayDto` records.
  - `tests/.../Specs/Issue/Api/IssueMetricsApiSpecs.cs` — delete the `CumulativeFlow_*` specs; keep the other metrics-endpoint specs intact.
  - `StagePopulationSnapshotService` (the daily snapshot writer) and the `StagePopulationSnapshots` table: no reader remains after this change; whether to also remove the writer + table (and its hosting schedule) is decided in design.md against any non-insights dependency.
- **Retained, untouched**: `useCostRollup` (endpoint + hook, consumed by `FactoryStatusHeadline`), `useEpics` (endpoint + hook, consumed by `EpicListPage`), all retained chart components' internals, the Epics list page, the range selector.
- **No runner / CLI / database schema migration required for the removals themselves** (the snapshot table, if kept, is simply unread; if dropped, that migration is scoped in design.md). No new dependencies.
- **Risk** (low): pure subtraction and reordering; no retained chart's calibration or backend contract changes. The only breaking surface is the removed `cumulative-flow` HTTP route, whose sole consumer is being deleted in the same change.
- Verification: `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`, and `npm test` (server) must pass.
