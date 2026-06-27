## Context

The Dashboard's `Productivity` slot currently renders as an empty placeholder (`DashboardPage.tsx:86`, the else-branch renders `<DashboardZone>` with no children). There is no feedback loop on AI quality: a user cannot tell whether issues ship right the first time or whether a particular stage repeatedly triggers repair. Without that signal, quality drift (model regression, bad prompt, over-complex issue) stays invisible until runs pile up as failures.

The data needed to compute this signal **already exists**: every check on every stage run carries a `RepairCount` (`StageCheck.cs:36`), incremented by `StageRun.ScheduleCheckRepair` and the run's `AddRepairTask` helper. What is missing is (1) a backend aggregation that reads those counts and classifies issues as first-time-right vs. reworked over trailing windows, and (2) a frontend panel that renders the result.

This change follows two established verticals in the same codebase:
- **Completion metrics** — `GET /metrics/completion` → `IssueQuerier.GetCompletionBucketsAsync` → `useCompletionTrend` → `CompletionTrend`. This is the precedent for **event-sourced ship-time windowing** (`IssueEvents.Time` of `com.mohist.issue.work-completed`).
- **Approval-wait metrics** — `GET /metrics/approval-wait` → `IssueQuerier.GetApprovalWaitAsync` → `useApprovalWait` → `AttentionHero`. This is the precedent for **load-issues → load-run-state → aggregate-in-window** and for nullable-stats zero-sample handling.

Constraints:
- **No new data collection** (spec-hard): the endpoint computes purely from existing `RepairCount` and existing ship events. No new domain event, no new column, no workflow write.
- **No version compatibility** concern (per AGENTS.md — project is in active development).
- Repair counts live on the **raw `WorkflowRun` aggregate**, but the existing bulk loader (`IssueQuerier.LoadWorkflowStatesAsync`) projects through `WorkflowStatusMapper.BuildStatusView`, which **drops `RepairCount`**. This fork in the road is the central design decision below.

## Goals / Non-Goals

**Goals:**
- Expose `GET /api/projects/{projectRef}/issues/metrics/quality` returning first-time-right rate and per-stage rework rate, each for trailing 7d and 30d windows, computed from existing `RepairCount` + ship events.
- Return a **zero-sample-distinguishable** result per window (and per stage) so the UI can render "no data yet" rather than a misleadingly perfect score.
- Render a `QualityPanel` inside the `Productivity` zone that sources rates exclusively from the new endpoint (never computing client-side over the full run set).
- Cover aggregation logic, endpoint contract, and panel rendering (populated + empty) with tests paralleling the approval-wait / completion suites.

**Non-Goals:**
- No per-model or per-prompt drill-down (total trend only — per proposal).
- No quality alert thresholds.
- No per-session granularity (issue/stage aggregation only).
- No change to how/when `RepairCount` is written — this change is read-only over existing data.
- No widening of the workflow-run status API contract beyond what the metrics path needs.

## Decisions

### D1. Endpoint placement and route

New partial `IssueRoutes.QualityMetrics.cs`, route `GET /api/projects/{projectRef}/issues/metrics/quality`, wired at `IssueRoutes.cs:19-20` next to `MapIssueMetrics()` / `MapIssueApprovalMetrics()`. Handler shape mirrors `IssueRoutes.ApprovalMetrics.cs:12-25` (inject `IssueQuerier`, resolve project via the existing `{projectRef}` + `ProjectResolutionEndpointFilter`, return `ApiResults.Ok(BuildResponse(...))`).

**Rationale:** spec mandates co-location with the existing project metrics surface so a dashboard fetches the summary in one read. No query params (both windows returned together, unlike completion's `?bucket=`).

**Alternative considered:** a single combined `/metrics/dashboard` endpoint returning completion + approval-wait + quality. Rejected — each metric already has its own endpoint and its own hook/cache key; combining them couples independent refresh rates and breaks the established one-endpoint-per-metric pattern.

### D2. Aggregation method and data-access path

New `IssueQuerier.GetQualityAsync(projectId, now)` sits next to `GetApprovalWaitAsync` (`IssueQuerier.cs:364`). It reuses the load-project-issues → load-run-states skeleton but, unlike approval-wait, reads the **raw deserialized `WorkflowRun`** (not the projected `WorkflowStatusView`) so it can see `StageCheck.RepairCount`.

Add a sibling bulk loader `LoadWorkflowRunsAsync(db, issues) → Dictionary<string, WorkflowRun>` next to `LoadWorkflowStatesAsync` (`IssueQuerier.cs:591`), reusing the existing `DeserializeRun` (`IssueQuerier.cs:986`). The existing view-projecting loader stays untouched.

**Rationale (the central decision flagged in `proposal.md:23`):** `RepairCount` is dropped by `WorkflowStatusMapper.MapChecks` (`WorkflowStatusMapper.cs:128-138`) and absent from `CheckStatusView` (`WorkflowViews.cs:107-113`). Two ways to reach it:

| Option | Change | Trade-off |
|---|---|---|
| **A.** Add `RepairCount` to `CheckStatusView` + both `MapChecks` branches | Widens the frontend-facing workflow status contract for a metrics-only need | One field, additive; but every status-API consumer now sees a control-plane counter that only metrics use today |
| **B. (chosen)** Add `LoadWorkflowRunsAsync`, read raw `WorkflowRun.Stages[].Checks[].RepairCount` directly | Metrics method owns its data shape; projection layer untouched | Duplicates the bulk-load+deserialize loop (trivial — `DeserializeRun` is shared) |

Chose **B** because: (1) AGENTS.md mandates "data model should be as concise as possible, only necessary properties" — `RepairCount` is not needed by any current status-API consumer, only by the metrics aggregator; (2) it keeps the quality method authoritative and self-contained, exactly like `GetCompletionBucketsAsync` reads `IssueEvents` directly rather than going through a view; (3) the raw run also carries stage-entry and per-check presence info the view also discards, all of which the classification needs.

### D3. Ship-time anchor (window membership)

Anchor window membership on **`IssueEvents.Time` of `com.mohist.issue.work-completed`** for issues whose status is `Done` — exactly the predicate `GetCompletionBucketsAsync` already uses (`IssueQuerier.cs:217, 302-312`). An issue contributes to a window iff that event's time falls in `[now - W, now]`.

**Alternatives considered:**
- `WorkflowRun.CompletedAt` (`WorkflowRun.cs:47`) — tempting because it is on the run state already loaded by D2, but **rejected**: run completion is not semantically identical to the issue reaching `Done` (manual status changes, transition failures). The spec is explicit ("reached Done within that window"), and reusing the proven event-sourced anchor keeps the two dashboard metrics consistent.
- Add `CompletedAt` to the `Issue` domain/read model — rejected as borderline "new state" and unnecessary when the event already exists.

### D4. Single-pass classification, dual-window bucketing

Classify each shipped issue **once**, then assign it to the 7d bucket, the 30d bucket, or both, based on ship time (an issue shipped 3 days ago lands in both; 20 days ago in 30d only; 40 days ago in neither):

```
for each Done issue in project:
  shipTime = IssueEvents[work-completed].Time
  run      = LoadWorkflowRunsAsync[issue.WorkflowRunId]
  isFtr    = run.Stages.All(s => s.Checks.All(c => c.RepairCount == 0))
  perStageReworked = { stage: stage.Checks.Any(c => c.RepairCount > 0) for each entered stage }
  for W in [7d, 30d]:
    if shipTime >= now - W:
      window[W].sampleCount++
      if isFtr: window[W].ftrCount++
      for each entered stage s:
        window[W].stage[s].entered++
        if perStageReworked[s]: window[W].stage[s].reworked++
```

**Rationale:** avoids recomputing classification per window; the two windows share the expensive part (classification) and differ only on the cheap part (time bucketing). "Entered a stage" = the `StageRun` is present in the run's `Stages` collection with at least one check that has been started (non-pending); the exact predicate is pinned by tests against seeded run states (see Risks).

### D5. Zero-sample representation

Rates are **nullable** with an explicit integer denominator, mirroring `ApprovalWaitMetricsResponse`'s nullable `AverageSeconds` etc. (`IssueRoutes.Dtos.cs:253-262`):

```csharp
public sealed record QualityMetricsResponse(
    QualityMetricsWindowDto Window7d,
    QualityMetricsWindowDto Window30d);

public sealed record QualityMetricsWindowDto(
    string From, string To,
    int SampleCount,                 // shipped issues in window (FTR denominator)
    double? FirstTimeRightRate,      // null iff SampleCount == 0
    StageReworkRateDto[] Stages);

public sealed record StageReworkRateDto(
    string Stage,
    int EnteredCount,                // shipped issues that entered this stage (denominator)
    double? ReworkRate);             // null iff EnteredCount == 0
```

`SampleCount == 0 && FirstTimeRightRate == null` is distinguishable from a genuine rate of `1.0` (`SampleCount > 0 && FirstTimeRightRate == 1.0`). Per-stage emptiness is independent (`EnteredCount == 0` for a stage nobody reached). Both windows are evaluated independently, per spec scenario.

**Alternative considered:** sentinel values (`-1`) for empty. Rejected — nullable doubles are the established pattern and are type-safe.

### D6. Frontend composition

1. New `useQualityMetrics` hook at `packages/web/src/entities/issue/api/quality-metrics.ts`, mirroring `useApprovalWait` (`approval-wait.ts`): `useProject()` → `useQuery({ queryKey: ['issues','metrics','quality',projectId], queryFn: fetch, enabled: !!projectId, staleTime: 60_000 })`. Export via the `entities/issue/index.ts` barrel. **No invalidator helper** — this is a read-only trailing-window metric with no user action that invalidates it (same as `useCompletionTrend`, which also lacks one).
2. New `QualityPanel` at `packages/web/src/pages/dashboard/productivity/QualityPanel.tsx` — zero-arg component calling `useQualityMetrics()`, branching on `data.window7d.sampleCount === 0` (and per-stage `enteredCount === 0`) to the established empty-state convention: outer `<section data-state="empty">` + `<p data-testid="productivity-quality-empty">` with human copy (mirrors `CompletionTrend.tsx:95-118` and `InvestmentPanel.tsx:67-76`).
3. Add `<QualityPanel />` into the existing `ProductivityZone` (`ProductivityZone.tsx`) alongside `CompletionTrend` / `InvestmentPanel` — matching the proposal's "alongside" wording.
4. **Wire `ProductivityZone` into the dashboard**: `DashboardPage.tsx:86`'s else-branch currently renders `<DashboardZone ... />` with no children; change it to `<DashboardZone ...><ProductivityZone /></DashboardZone>`. This unblocks the dead-code `ProductivityZone` (defined but never imported today) and satisfies the modified `dashboard-shell` requirement that the `Productivity` slot no longer render empty.
5. Update `DashboardPage.test.tsx:141-177` which currently asserts `productivity.childElementCount === 0` and `productivity-zone` is absent — flip both assertions.

**Alternative considered:** mount `QualityPanel` directly into the slot without reviving `ProductivityZone`. Rejected — the proposal explicitly says "alongside `InvestmentPanel` and `CompletionTrend`", and those siblings already live inside `ProductivityZone`. Reviving the zone is the intended composition.

## Risks / Trade-offs

- **[Full-project scan cost]** `GetQualityAsync` loads every project issue + every run state, same as `GetApprovalWaitAsync`. → *Mitigation:* accepted at current scale (precedent is already shipped). Single-pass classification (D4) means the expensive part runs once, not twice for the two windows. If this later bites, the ship-time filter can be pushed into the SQL layer (only load runs for issues with a recent `work-completed` event) — a pure optimization, no contract change.
- **["Entered a stage" predicate ambiguity]** Whether a `StageRun` with zero checks means "not yet entered" vs "entered but no checks defined" is not 100% pinned by existing code. → *Mitigation:* the quality tests seed run states covering both an entered-and-repaired stage, an entered-and-clean stage, and a never-reached stage, locking the predicate against the spec scenarios (`ai-quality-metrics/spec.md:56-60`).
- **[`ProductivityZone` is currently dead code]** Wiring it in revives a component with no test and revives sibling panels (`InvestmentPanel`) that render their own "Data unavailable" placeholders. → *Mitigation:* acceptable — those placeholders already follow the empty-state convention and will fill in as their own metrics land; `ProductivityZone` gets a composition test as part of this change.
- **[Stale metric]** Trailing-window rates drift as ship times age out. → *Mitigation:* `staleTime: 60_000` on the hook matches the sibling metrics; no stronger invalidation needed for a read-only trend.
- **[Per-stage denominator skew]** Different stages have different denominators (few issues reach `integrate`). → *Not a risk, a feature:* the spec mandates independent per-stage rates (`spec.md:72-76`); the DTO surfaces `EnteredCount` so the UI can show "n=3" and avoid overstating a rate from a tiny sample.

## Migration Plan

**Deploy (additive, no breaking API change):**
1. Backend: add `LoadWorkflowRunsAsync`, `GetQualityAsync`, DTOs, `IssueRoutes.QualityMetrics.cs` partial, wire route. Ship behind no flag — it's a new read-only endpoint.
2. Web: add `quality-metrics.ts` hook + barrel export, `QualityPanel.tsx`, add to `ProductivityZone`, wire `ProductivityZone` into `DashboardPage.tsx:86`, update `DashboardPage.test.tsx`.
3. Spec: the `dashboard-shell` MODIFIED requirement (empty placeholder → `QualityPanel`) lands with this change.

**Rollback:** revert step 2's `DashboardPage.tsx` wiring (one line) to return the slot to empty; the backend endpoint can remain (harmless, unreferenced) or be reverted independently. The two layers are decoupled — the frontend degrades gracefully if the endpoint is absent (the hook errors, `QualityPanel` renders its empty state).

No data migration, no schema change, no event backfill — all data already exists.

## Open Questions

- **Stage set provenance:** should the per-stage breakdown list only the default workflow stages (`plan`/`build`/`check`/`integrate`) or every stage present in any run, including custom ones? *Working answer:* every stage present in the run state (generic) — the aggregation reads from `WorkflowRun.Stages`, not a hardcoded list, so custom stages surface automatically. The DTO is an array, not a fixed shape, so this needs no contract decision now.
- **Loading state UX:** should `QualityPanel` show a distinct `data-state="loading"` skeleton (like `DashboardDigestWidget` does) or treat pending + empty identically? *Working answer:* mirror `CompletionTrend`, which does not distinguish loading from empty — simplest, and `staleTime: 60_000` keeps refetches rare. Revisit if users report confusion.
