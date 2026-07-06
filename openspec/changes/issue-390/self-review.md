# Self Review Report

## Result: PASS

## Repaired Items

None. The plan artifacts were verified against the codebase and required no direct repairs:

- File paths and line numbers in `proposal.md` / `design.md` are accurate: `IssueRoutes.cs:25` (`MapIssueCumulativeFlow()` call), `MohistServiceRegistration.cs:86-90` (writer hosting block), `MohistDbContext.cs:71` + `722-733` (DbSet + entity config), `MohistIntegrationFixture.cs:242-260` (raw `CREATE TABLE` block), `CumulativeFlowQuerier.cs:79` (sole table reader).
- The retained-hook claim holds: `useCostRollup` is consumed by `widgets/factory-status/ui/FactoryStatusHeadline.tsx:21` and `useEpics` by `pages/epics/ui/EpicListPage.tsx:383`, so D2's decision to keep them is correct.
- The shared-attribution claim holds: `IssueStageAttribution` is referenced 10× by the retained `IssueMetricsQuerier.cs`, so D4's "keep the attribution core" boundary is correct.
- The `SeedStagePopulationSnapshotAsync` helper is used only by `CumulativeFlow_*` specs in `IssueMetricsApiSpecs.cs`, so deleting those specs orphans it as T-001 describes.
- The current `InsightsCharts.tsx` output group matches the plan's subtraction model exactly (`EpicProgressList`, `ThroughputChart`, `CompletionTrend`, `CumulativeFlowChart`); T-001 + T-002 reduce it to the spec-mandated `Throughput`, `Completion Trend`.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: consistency
  Evidence: T-003 points its `spec` field at `specs/cumulative-flow-metrics/spec.md`, but that spec pins only the **read surface** (endpoint, querier, DTOs, hook). The writer + table + hosting removal T-003 implements is backed solely by design D4; no spec scenario asserts "the snapshot writer MUST be removed." This is non-blocking because (a) T-003's `notes` already explicitly cite "依 design D4", making the backing transparent, and (b) the writer removal is a design-driven dead-code cleanup, not a product requirement, so it does not strictly require spec coverage. The `cumulative-flow-metrics` capability is also the closest semantic anchor for "cumulative-flow is gone from the system."
  SuggestedAction: Optionally add one scenario to `cumulative-flow-metrics/spec.md` pinning that no `StagePopulationSnapshotService` writer / `StagePopulationSnapshots` table remains after the read surface is removed, so T-003 has an exact spec match rather than a best-fit pointer. This is a spec enrichment, not a correctness fix, and is safe to defer.
  Status: follow-up

<promise>PASS</promise>
