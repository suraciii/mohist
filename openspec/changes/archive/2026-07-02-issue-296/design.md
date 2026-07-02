## Context

The Dashboard reduces "一次做对" quality to two fixed single-point scalars (7-day and 30-day first-time-right rate) in `QualityPanel`. The operator cannot tell whether first-time-right ability is improving or degrading as output scales — exactly the "rising throughput but falling FTR" signal a scalar hides. See `proposal.md` for motivation and `specs/` for requirements.

Relevant current state (verified in code):

- **The FTR / rework classification already exists.** `IssueQuerier.GetQualityAsync` (`IssueQuerier.cs:499`) loads each project issue + its lifecycle workflow runs + durable run-event facts, then `ClassifyRuns` (`IssueQuerier.cs:623`) single-pass classifies every shipped (`Done`) issue into `(bool IsFirstTimeRight, IReadOnlyDictionary<string,bool> StageRework)`. `StageRework[stage]` is true when at least one check in that stage has `RepairCount > 0` (or a `RepairScheduled` event / repeated check-run appears in the durable facts).
- **Window membership is already anchored on ship time** — the latest `com.mohist.issue.work-completed` event time per issue (`IssueQuerier.cs:553-557`). Each shipped issue is accumulated into the 7d and/or 30d trailing window via `Accumulate` (`IssueQuerier.cs:699`) → `QualityAccumulator` (`IssueQuerier.cs:691`).
- **The endpoint and DTOs already exist.** `GET /api/projects/{projectRef}/issues/metrics/quality` (`IssueRoutes.QualityMetrics.cs:12`) returns `QualityMetricsResponse { Window7d, Window30d }` (`IssueRoutes.Dtos.cs:264`); each window carries `SampleCount`, nullable `FirstTimeRightRate`, and per-stage `StageReworkRateDto[]`. The web consumes it via `useQualityMetrics` (`entities/issue/api/quality-metrics.ts:35`, `staleTime: 60_000`) and renders `QualityPanel.tsx`.
- **A daily bucketing idiom already exists.** `GetCompletionBucketsAsync` (`IssueQuerier.cs:345`) emits 30 trailing UTC days, **pre-sizing the boundary array so empty days are represented as zero buckets** (not dropped), anchored on event time. `ThroughputChart` and `CostTrendChart` both render daily bars over this kind of window.
- **The reusable chart baseline already exists** (established by the prerequisite 294): `pages/dashboard/charts/` — `ChartContainer` (loading/error/empty three-state wrapper, `ChartContainer.tsx`), `ChartAccessibility` (SR data summary + shape-disambiguated legend, `ChartAccessibility.tsx`), `LineSeries` (**accepts `(LinePoint | null)[]` and splits into segments around `null` gaps**, `LineSeries.tsx:15`), `ChartAxes`, `ChartLegend`, `useReducedMotion`. Theme tokens `--chart-1..5`, `tabular-nums`, transform-based motion.
- **The FTR trend is a pure read.** It introduces no new event, state collection, or workflow-domain write (hard constraint in both specs). The data — per-check `RepairCount` + the ship event — is already recorded.

Constraints / stakeholders:

- Per `design/architecture.md`: Web UI only renders state; authoritative interpretation lives in the Server. The widget is read-only.
- The change is strictly additive: the existing 7d/30d single-point contract and the zero-sample empty result MUST remain unchanged (both specs pin this).
- Reuses the chart baseline — introduces **no** new chart library or primitive.

## Goals / Non-Goals

**Goals:**

- Extend the existing AI-quality surface with a per-time-bucket FTR series and a per-time-bucket rework series, computed by reusing the existing classification verbatim — no second quality computation path.
- Render a per-bucket FTR trend line in the Productivity zone, with an optional rework-rate overlay on the same percentage axis, composed against the established chart baseline.
- Full loading/error/empty states with a concrete next-action empty state; null-rate (empty) buckets plot no numeric point.
- Preserve every existing contract unchanged; the per-bucket series is strictly additive on the wire.

**Non-Goals** (per proposal/specs):

- No per-stage FTR time series, no FTR drill-down.
- No change to the `QualityPanel` 7d/30d scalar figures or their presentation.
- No new events, domain writes, or data collection.
- No new chart library or baseline primitive.

## Decisions

### D1. Co-locate the trend on the existing `/metrics/quality` response as one additive `Trend` field spanning the 30-day window.

The spec requires the per-bucket series to be "co-located with the existing project AI-quality aggregation, so a dashboard can read the trend in the same surface it already reads for the single-point quality summary." Extend `QualityMetricsResponse` (`IssueRoutes.Dtos.cs:264`) with a single sibling `Trend` object covering the **30-day** window (the longer of the two the surface already evaluates) with **daily** buckets:

```
record QualityTrendDto(
    string Bucket,                 // "day"
    string From, string To,        // matches Window30d.From/To
    QualityTrendPointDto[] Points) // length 30, pre-sized

record QualityTrendPointDto(
    string Boundary,               // "yyyy-MM-dd"
    int SampleCount,
    double? FirstTimeRightRate,    // null iff SampleCount == 0
    double? ReworkRate)            // null iff SampleCount == 0; reworked-at-any-stage / all-shipped
```

One trend (not two): the 7-day single-point is just the last 7 buckets' summary, so a separate 7-day bucketed trend would be redundant. The 30-day span gives the operator the full recent history next to the daily `ThroughputChart` / `CostTrendChart`, while the 7d/30d scalars remain the at-a-glance summary.

Alternatives considered:
- **Separate `/metrics/quality/trend` route.** Rejected — the spec explicitly wants co-location in one read, and the trend is computed in the same single pass as the scalars (D4), so splitting it forces either a second full scan or a contrived shared cache. One endpoint, one fetch.
- **Per-window trends (a `Trend` inside each `Window7d`/`Window30d`).** Rejected — doubles the payload with a redundant 7-day series, and couples the trend shape to the scalar window abstraction it does not need.

### D2. Bucket granularity = daily, pre-sized 30-point boundary array; empty buckets yield the null rate, not zero.

Mirror `GetCompletionBucketsAsync` (`IssueQuerier.cs:345-394`): 30 trailing UTC days inclusive of today, boundaries pre-sized so **every day is emitted** — an empty day is a `QualityTrendPointDto` with `SampleCount == 0` and **null** rates (the spec's "defined empty result, distinguishable from a genuine FTR rate of one or zero, evaluated independently per bucket"). Pre-sizing matters: if only non-empty buckets were emitted, the chart x-axis would compress and the operator could not read cadence. Membership is anchored on ship time exactly as the existing scalars are (`com.mohist.issue.work-completed`, latest wins), so non-shipped issues contribute to no bucket.

Rationale for **daily** over weekly:
- The issue's whole point is putting FTR into the **same time dimension as throughput** ("吞吐上升但 FTR 下降"). `ThroughputChart` is daily; a daily FTR trend aligns point-for-point with it. A weekly FTR line beside a daily throughput bar chart would not visually align and would dull the cross-chart signal.
- The spec explicitly accepts sparse buckets — empty days plot **no numeric point** (D5), not a misleading 0%/100%.

Alternatives considered:
- **Weekly buckets** (12 ISO weeks, like `GetCompletionBucketsAsync`'s week mode). Gives larger, stabler per-bucket denominators, but loses day-level alignment with the throughput trend and only yields ~4 points over a 30-day window. Reconsider if real projects ship so rarely that the daily line is mostly gaps — but gaps are honest, not broken.
- **Configurable day/week granularity** (a query param). Rejected as scope creep; the issue leaves granularity open ("周或日") and one consistent choice keeps the dashboard coherent. The DTO carries `Bucket: "day"` so a future weekly mode is additive, not breaking.

### D3. Per-bucket rework rate = reworked-at-any-stage, derived from the existing per-stage classification — no new classification path.

The existing surface exposes **per-stage** rework rates. The trend spec wants a single **issue-level** rework rate per bucket: the share of shipped-in-bucket issues that were reworked at **at least one** stage they entered (an issue reworked at multiple stages counts **once**). This is a pure reduction of the already-computed `StageRework` dictionary:

```
bool reworkedAtAnyStage = stageRework.Values.Any(v => v);   // from ClassifyRuns, no new reads
```

`ClassifyRuns` (`IssueQuerier.cs:623`) already returns `StageRework` per issue, so the any-stage reduction is one line over data already in hand — no new event scan, no new classification. An issue with an unknown/missing run is classified not-first-time-right and contributes an empty `StageRework` (`IssueQuerier.cs:607-609`), so `reworkedAtAnyStage` is `false` for it (it cannot be proven reworked) — consistent with the existing conservative classification.

### D4. Server: bucket in the same single pass as the scalars — no extra data movement.

`GetQualityAsync` already loads every project issue + lifecycle runs + event facts **once** and iterates each shipped issue to classify and accumulate into the 7d/30d windows (`IssueQuerier.cs:580-616`). The trend is produced by adding, alongside `window7d`/`window30d`, a pre-sized `QualityTrendAccumulator[30]` indexed by ship-day boundary:

```
foreach (var issue in issues) {
    if (issue.Status != Done) continue;
    if (!shipTimes.TryGetValue(issue.Id, out var shipTime)) continue;
    var (isFirstTimeRight, stageRework) = ClassifyRuns(...);   // unchanged
    var reworkedAtAnyStage = stageRework.Values.Any(v => v);

    if (in7d) Accumulate(window7d, ...);                       // unchanged
    if (in30d) Accumulate(window30d, ...);                     // unchanged

    // NEW: one extra in-memory bucket lookup + increment
    var day = DateOnly.FromDateTime(shipTime.UtcDateTime.Date);
    if (indexByBoundary.TryGetValue(day, out var idx))
        Accumulate(trendBuckets[idx], isFirstTimeRight, reworkedAtAnyStage);
}
```

The expensive parts (issue/run/event loading, `ClassifyRuns`) run **exactly once** — the trend is another in-memory accumulator fed from the same classification. `BuildTrend` then maps each bucket to a `QualityTrendPointDto` with null rates when `SampleCount == 0` (mirroring `BuildWindow`'s nullability rule, `IssueQuerier.cs:720-722`). `now` stays an injected `DateTimeOffset` parameter (the endpoint passes `DateTimeOffset.UtcNow`, `IssueRoutes.QualityMetrics.cs:22`); tests inject it — no wall clock inside the method.

### D5. Web: one `FtrTrendChart` widget composes against the baseline; single percentage axis; overlay toggle is local state; null buckets gap the line.

New `pages/dashboard/productivity/FtrTrendChart.tsx`, mounted in `ProductivityZone.tsx` right after `QualityPanel` (so the trend sits beside its scalar sibling). It calls the **existing** `useQualityMetrics()` hook — one fetch yields scalars + trend. The DTO gains an optional `trend?` field (additive on the wire; old web ignores it).

Rendering:
- **Status** via `ChartContainer` (`status = isLoading ? 'loading' : isError ? 'error' : noBucketHasSamples ? 'empty' : 'resolved'`). Empty-state next action: "First-time-right trend appears once an issue ships within the trailing window."
- **Resolved** via `ChartAccessibility`: `role="img"` SVG + `sr-only` summary (window, first/last FTR, peak rework day) + shape-disambiguated legend.
- **Single 0–100% y-axis** (both series are percentages on the same scale — unlike `CostTrendChart`'s dual axis, there is no unit mismatch here). Fixed ticks at 0/25/50/75/100; no data-driven `niceCeil`.
- **FTR line** = `LineSeries` with `points: (LinePoint | null)[]` — a `null` entry where a bucket's FTR is null **gaps the line** (`LineSeries.splitSegments`, `LineSeries.tsx:15`) rather than plotting 0/100. This is exactly the spec's "a bucket with no shipped issues plots no numeric point for either series."
- **Rework overlay** = a second `LineSeries` on the same axis, toggled by local `useState`. Legend uses **shape** to disambiguate (FTR = solid line+marker swatch; rework = dashed-line swatch via `ChartLegend`'s `dashedLine` shape), so a user who cannot perceive color can still tell the two apart — the spec's non-color-legend requirement. Colors strictly from `--chart-*` tokens (e.g. FTR `stroke-chart-5`, rework `stroke-chart-4`).
- `tabular-nums` on all numeric labels; motion via `useReducedMotion` (inherited from `LineSeries`).

Alternatives considered:
- **Dual axis.** Rejected — both series are proportions in [0,1]; a single percentage axis is honest and simpler.
- **Derive rework client-side from FTR (`1 − FTR`).** Explicitly rejected by the spec — FTR and rework-at-any-stage are **not** complements (an issue can be not-first-time-right yet the rework signal lives in a stage classification); the rework rate MUST come from the per-bucket rework series.
- **A separate `useQualityTrend` hook + fetch.** Rejected — co-located on one endpoint (D1), so one hook/one query already covers it. Adding a second query for the same URL would double the fetch.

### D6. Read-only, additive end-to-end.

The widget performs no write/mutation against issue, session, or workflow state; the backend change is an additive read over already-recorded `RepairCount` + ship events. No migration, no event, no new column.

## Risks / Trade-offs

- **Daily FTR over 30 days is sparse for low-volume projects** (many days ship 0–1 issues → null gaps or extreme 0/100 points). Mitigated by null-gap rendering (empty buckets plot nothing, never a misleading 0%/100%) and by the 7d/30d scalars in `QualityPanel` for the at-a-glance summary. Accepted: the trend's job is the time-dimensional shape, and the scalars remain for the point estimate.
- **An issue reworked at multiple stages counts once in the rework numerator** — by design (D3, pinned by spec scenario), but it means the per-bucket rework rate is NOT the sum of per-stage rework rates and cannot be cross-checked by adding the `StageReworkRateDto[]` figures. Mitigated by a spec test pinning the "counts once" scenario.
- **`QualityMetricsResponse` record positional ctor changes** (new `Trend` parameter) — internal to the server; on the wire it is an additive JSON property, so an old web build ignores `trend` and an old server omitting it leaves the TS `trend?` field `undefined` (the widget treats that as empty). Safe either direction.
- **EF Core SQLite cannot translate `DateTimeOffset` against the TEXT `Time` column** (documented at `IssueQuerier.cs:409-414`) — the trend inherits the existing "fetch candidate rows, filter/bucket in memory" approach. Bounded by single-project scale on a local tool; accepted (same precedent as the scalars and completion buckets).
- **Full-project scan cost** — unchanged by this change; the trend adds only an in-memory accumulator to the scan that already runs. If it later bites, the ship-time filter can be pushed into SQL (a pure optimization, no contract change).

## Migration Plan

1. **Server first (additive, safe):** extend `GetQualityAsync` with the pre-sized daily trend accumulator + `BuildTrend`, add `QualityTrendDto` / `QualityTrendPointDto` to `IssueRoutes.Dtos.cs`, and map it in `IssueRoutes.QualityMetrics.cs`'s `BuildResponse`. Existing `Window7d`/`Window30d` shape is untouched. Deploy via `mo update server`.
2. **Server tests:** add `IssueQuerierSpecs` cases (per-bucket FTR rate, per-bucket rework-any-stage rate, multi-stage-counts-once, empty-bucket null independent of siblings, non-shipped excluded, ship-time anchoring, additive-unchanged scalars) and `IssueMetricsApiSpecs` cases (trend alongside windows, zero-sample `200` with null per-bucket, 404). Reuse the existing `SeedIssue` / `SeedEvent(WorkCompletedType, time, runId)` / `SeedWorkflowRunAsync(QualityRunState(...))` helpers.
3. **Web:** add `QualityTrendDto` / `QualityTrendPointDto` to `entities/issue/api/quality-metrics.ts` (optional `trend?` on the response), build `FtrTrendChart.tsx`, mount it in `ProductivityZone.tsx` after `QualityPanel`. Add `FtrTrendChart.test.tsx` (loading/error/empty/resolved, trend values from the series, overlay toggle, null-bucket no-point, legend shape disambiguation, SR summary). Deploy via `mo update` (server already has the field).
4. **Rollback:** additive end-to-end. Revert the web widget to hide the chart; revert the server `Trend` field to drop the series (scalars still render from the unchanged windows). No schema migration to reverse; the two layers degrade gracefully — if the endpoint omits `trend`, the widget renders its empty state.

Verification gates: `npm test` (server, C# warnings-as-errors), `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`.

## Open Questions

- **Bucket granularity in practice:** daily aligns with the throughput trend, but if real projects ship < ~3 issues/week the daily FTR line will be mostly gaps. Confirm daily is acceptable once real data lands; switching to weekly later is additive (the DTO already carries `Bucket: "day"`), not breaking.
- **Trend window length:** pinned to 30 days to match `Window30d` and the daily-bucket chart siblings. If a longer horizon (e.g. 90 days) is wanted later, that is an additive window change — out of scope here.
