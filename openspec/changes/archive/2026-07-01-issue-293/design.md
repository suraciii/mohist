## Context

The Dashboard can already show *how many* issues ship per day (`ThroughputChart`/`CompletionTrend`) and *what they cost* (`CostTrendChart`), but gives no feedback on *how long* an issue takes or whether delivery speed is stable. Cycle time is derivable today with **no new data collection** — the three lifecycle anchors are already persisted:

- **Creation** — `Issue.CreatedAt` (`Issue.cs:58`), `init`-only, immutable across retries/reopens.
- **First work-start** — `com.mohist.issue.work-started` CloudEvent rows in the durable `IssueEvents` table (read the same way by `IssueQuerier.GetQualityAsync`, `IssueQuerier.cs:446-462`).
- **Completion** — `Issue.CompletedAt` (`Issue.cs:72`), defined by the `issue-completion-timestamp` spec as the *latest terminal `done` moment* (set on `Complete`/`Close`, retained across `Reopen`, overwritten on re-completion — `Issue.Transitions.cs:171-221`).

The reusable Dashboard chart baseline established by issue 294 (`archive/2026-06-30-issue-294/design.md`) is in place: a first-party SVG kit under `pages/dashboard/charts/` (`ChartContainer` three-state wrapper, `ChartAccessibility` SR-summary + shape legend, `BarSeries`/`LineSeries`/`ChartAxes`/`ChartLegend`, `useReducedMotion`), `--chart-*` theme tokens, `tabular-nums`, and `transform`/`opacity`-only motion. The closest sibling widget is `ThroughputChart` (issue 292), which added a new `SegmentedBarSeries` primitive **without touching `BarSeries`** and a client-side averaging helper in a unit-tested `model/throughput.ts`. This change composes against all of that.

Constraints / stakeholders:
- Per `design/architecture.md`: Web renders state and emits intent; the Server owns authoritative interpretation. The widget is read-only.
- This is a **pure additive read** over already-recorded events + the already-recorded `CompletedAt` — no new events, no schema change, no domain writes.
- The proposal note that completion metrics "already read `IssueCreated` events" is **imprecise**: existing surfaces read creation time off `Issue.CreatedAt`, not off `com.mohist.issue.created` events. Either is valid (creation is immutable); `Issue.CreatedAt` is the cleaner source and is what this design uses.

## Goals / Non-Goals

**Goals:**
- Expose a project-scoped **delivery-time surface** returning one entry per delivered issue (completion date, lead time, cycle time) over a fixed trailing window, derived purely from persisted lifecycle anchors and applying the first-work-start → final-completion rule for retries.
- Render a **cycle-time scatter control chart** in the Productivity zone: one point per delivered issue, overlaid rolling P50 and P85 percentile lines, with lead-time vs cycle-time lenses.
- Preserve every existing contract (`/metrics/completion`, `/metrics/quality`, `/metrics/approval-wait`) unchanged; the new surface is strictly additive.
- Route loading/error/empty through the shared `ChartContainer`; the empty state names a concrete next action.

**Non-Goals** (per proposal):
- No grouping/coloring by label or epic.
- No scatter-point click-through drill-down.
- No outlier annotation.
- No user-configurable window or rolling window.
- No DB-side aggregation refactor (independent tech debt; does not block this chart).

## Decisions

### D1. New sibling route `GET /issues/metrics/delivery-time`, not an extension of `metrics/completion`.

The server exposes the surface as a new project-scoped read co-located with the other issue metrics: `GET /api/projects/{projectRef}/issues/metrics/delivery-time`, mapped by a new `MapIssueDeliveryTimeMetrics()` partial registered next to its siblings in `IssueRoutes.cs:19-21`. Computation lives in a new `IssueQuerier.GetDeliveryTimesAsync(projectId, now)` method, co-located with `GetCompletionBucketsAsync`/`GetQualityAsync`/`GetApprovalWaitAsync` (it owns the issue/event-reading idiom already).

Alternatives considered:
- **Extend `CompletionMetricsResponse` with a sibling per-issue array** (issue-294's D5 sibling-array pattern). Rejected: completion metrics are *per-day buckets* — a dense day array is the wrong carrier for a *per-issue* series, and mixing the two on one DTO couples two unrelated lenses. The completion surface is keyed on bucketed days; this surface is keyed on individual issues. A separate route keeps each response shape single-purpose.
- **A generic `/issues/lifecycle-events` dump computed client-side.** Rejected: violates the "authoritative interpretation lives in the Server" rule and pushes retry/reopen/zero-sample semantics into N clients.

The route returns `404` for an unknown project (inherited from `ProjectResolutionEndpointFilter`) and `200` + the empty result for zero delivered issues, mirroring `ApprovalWaitResult` (`IssueQuerier.cs:797-805`). The `metrics` segment precedes `{number:int}` so it cannot collide with issue-number routes (existing convention).

### D2. Lead time from `Issue.CreatedAt`; cycle time from earliest `work-started`; completion anchor from `Issue.CompletedAt`.

- **Lead time** = `issue.CompletedAt - issue.CreatedAt`. Creation is read off the aggregate (`Issue.CreatedAt`, immutable) — not off `com.mohist.issue.created` events. This is simpler than an event scan and identical in result.
- **Cycle time** = `issue.CompletedAt - earliest(work-started event time for this issue)`. A single scan over `IssueEvents` rows where `Source ∈ projectSources && Type == WorkStartedType`, taking `Min(Time)` per issue — the same scan `GetQualityAsync` (`IssueQuerier.cs:446-462`) already does, but `Min` instead of tracking the latest.
- **Completion anchor** = `issue.CompletedAt` directly. This is *already* the latest terminal `done` moment per the `issue-completion-timestamp` spec, so reopen-and-re-complete is handled by the aggregate for free — no need to re-derive "latest `work-completed` event" the way `GetQualityAsync` does (that method predates / parallels the `CompletedAt` field). Reading `CompletedAt` is the cleaner path for a new surface.
- **Delivered population** = issues with `Status == Done` and a non-null `CompletedAt` in the window. `Cancelled` issues (`IssueClosed`) are excluded per spec.

Alternatives considered:
- **Read lead time from `com.mohist.issue.created` events** for symmetry with the event scan. Rejected: creation is immutable, `Issue.CreatedAt` is one lookup vs. an event-table scan, and it avoids the per-issue `Source` URI indirection for one of the two durations.
- **Re-derive completion from `work-completed` events** (as `GetQualityAsync` does). Rejected as redundant given `Issue.CompletedAt` already encodes the latest-terminal-moment rule.

### D3. Trailing window: 30 days keyed on completion time, time injected.

The surface is scoped to delivered issues whose `CompletedAt` falls within `[now - 30d, now]`. This matches the established server precedent: `GetCompletionBucketsAsync` (`IssueQuerier.cs:269-274`) and `GetQualityAsync` (`:405-407`) both use a 30-day window; the approval-wait surface uses 7 days. `now` is an injected `DateTimeOffset` parameter (never `DateTimeOffset.UtcNow` inside the method), satisfying the no-wall-clock testing rule and the spec's "window advances with current time" requirement.

The proposal mentioned "30/60 days"; **30 days is chosen** to align with the sibling completion/quality surfaces (one recent-delivery-speed lens, consistent window across the Productivity zone) and because the window is fixed/non-configurable. 60 days is a non-goal (deferred to a future configurability issue if needed).

### D4. Per-issue series DTO with explicit null-vs-zero semantics.

Internal result record on `IssueQuerier` (mirroring `ApprovalWaitResult`/`QualityMetricsResult`):

```
record DeliveryTimeResult(IReadOnlyList<DeliveryTimePoint> Points);
record DeliveryTimePoint(string IssueNumber, DateTimeOffset CompletedAt, double LeadDays, double? CycleDays);
```

- `LeadDays` is always present (creation and completion both exist for a delivered issue).
- `CycleDays` is `double?`: **`null`** = no recorded `work-started` (undefined, excluded from the cycle lens + percentile computation); **`0`** = a genuine same-moment zero-duration cycle (kept and plotted).
- Empty window → `Points` is empty (not an error, not a fabricated zero). The consuming chart distinguishes "empty" (zero points) from "all-zero durations" (points present) structurally — no `SampleCount` field is needed because the point list length *is* the sample count.

Wire DTOs (`IssueRoutes.Dtos.cs`), serializing `DateTimeOffset` as ISO-8601 `.ToString("o")` (matching `CompletionMetricsWindowDto`):

```
record DeliveryTimeMetricsResponse(IReadOnlyList<DeliveryTimePointDto> Points);
record DeliveryTimePointDto(string IssueNumber, string CompletedAt, double LeadDays, double? CycleDays);
```

`System.Text.Json` serializes `double?` `null` and `0` distinctly by default, satisfying the spec's "undefined distinguishable from genuine zero" scenarios without a discriminator.

### D5. Web hook under `entities/issue/api/`, percentile helper under `model/`.

- **Hook** — `entities/issue/api/delivery-time.ts`, mirroring `completion-trend.ts`: `useQuery({ queryKey: ['issues','metrics','delivery-time',projectId], queryFn: () => fetchDeliveryTime(projectId!), enabled: !!projectId, staleTime: 60_000 })`, re-exported from `entities/issue/index.ts`. `request<T>` unwraps the `{success,data}` envelope; `projectApiPath(projectId, '/issues/metrics/delivery-time')` builds the route.
- **Percentile helper** — `pages/dashboard/productivity/model/delivery-time.ts` (pure functions, co-located `.test.ts`), mirroring `model/throughput.ts` (issue 292's `computeMovingAverage`). Exports `computeRollingPercentile(samples, window, p)` over the per-issue series ordered by completion date. Rationale for a pure-module helper (vs. inline in the component): edge cases — partial leading window, undefined-exclusion per lens, even-sample percentile interpolation — are directly unit-testable without rendering the chart.

### D6. Add a `ScatterSeries` primitive to the kit (sibling, not inline); single days y-axis.

The chart kit has `BarSeries`/`LineSeries`/`SegmentedBarSeries` but **no scatter primitive**. Following issue 292's `SegmentedBarSeries` precedent ("does not perturb the shared `BarSeries`… zero regression risk"), add a **new sibling `ScatterSeries`** under `pages/dashboard/charts/` (+ co-located `.test.ts`), exported from `charts/index.ts`, rendering one `<circle>` per point with `transform`/`opacity`-only motion honoring `useReducedMotion`. Existing primitives are untouched.

Alternatives considered:
- **Inline `<circle>` elements directly inside `ChartAccessibility`** (as `CostTrendChart` inlines its `<text>` labels). Rejected for a *series* of points: motion, clipping, and hit-padding belong in one place, and a kit primitive keeps the widget focused on layout (consistent with how `LineSeries` owns the P50/P85 paths).
- **Reuse `LineSeries` with zero-length segments / markers only.** Rejected: `LineSeries` is a connected path; forcing it to render disconnected dots (via `null` gaps) misrepresents the model and fights its gapping semantics.

Unlike `CostTrendChart`'s dual axis (D6 there: cost vs cost-per-ship have different units), this chart has **one unit — days** for scatter, P50, and P85, so a **single left axis** suffices (mirrors `ThroughputChart`'s single count axis).

### D7. Rolling percentile: fixed issue-count window, computed client-side, partial at the series start.

The P50/P85 lines are computed **client-side** from the per-issue series (the surface returns per-issue samples, never a pre-aggregated percentile — spec requirement). For each position along the completion-date axis (ordered ascending), the percentile value is computed over the **trailing rolling window of the last N issues** ending at and including that position:

- `computeRollingPercentile(samples, window=N, p)`: for position `i`, take `samples[max(0, i-N+1) .. i]`, exclude entries whose duration for the current lens is `null`, then compute the percentile. Near the series start (fewer than N prior issues) it computes over the available issues up to and including `i` — lines plot from the first valid sample, never omitted solely because the window isn't full (spec scenario).
- **Percentile method**: nearest-rank for P50 (i.e. the median — average of the two middle values for even counts, the conventional median), and linear-interpolation between closest ranks for P85 (the conventional percentile convention). Pinned in the helper so the test is deterministic.
- **Window size N**: fixed (not user-configurable, per spec). Chosen as **10 issues** — small enough that the percentile line reacts to recent shifts on a local tool with modest throughput, large enough that the line isn't jittery. This is a rolling *issue-count* window, distinct from D3's *time* window (30 days bounds the population; N bounds the rolling statistic).

The percentile computation is recomputed over the selected lens's durations when the operator switches lead ↔ cycle, excluding `null`-cycle issues from the cycle-lens percentile (they still plot under lead).

Alternatives considered:
- **Time-based rolling window** (e.g. issues completed in the last 7 days). Rejected: couples percentile resolution to throughput (a quiet week → tiny/dropout sample); issue-count window gives a stable rolling sample size.
- **Server-side percentile**. Rejected: the spec explicitly requires client-side derivation from the per-issue series; the surface is per-issue granularity precisely so the chart owns the percentile math.

### D8. Lead/cycle lens is a client-side toggle over the single surface.

The widget fetches the per-issue series once and exposes a lead-time/cycle-time toggle. Selecting the cycle lens excludes `CycleDays == null` issues from both the scatter and the P50/P85 recomputation; the lead lens shows all points. The toggle is widget-local UI state (no query-key change, no refetch). A lens with zero plottable points (e.g. cycle lens when *no* delivered issue has a work-start) renders the empty state via `ChartContainer`.

### D9. x-position by completion time, never edit time; reopen re-positions at latest completion.

Scatter x-position is driven by `CompletedAt` (sourced from the surface). A post-completion edit that bumps `UpdatedAt` cannot move/add/resurface a point (the surface never reads `UpdatedAt`). A reopened-and-re-completed issue positions at its new `CompletedAt` (the latest terminal moment) and does not retain a point at the prior completion — guaranteed by `Issue.CompletedAt` semantics (D2), not by chart logic.

## Risks / Trade-offs

- **Grayscale `--chart-*` palette with 3+ distinguishable elements** (scatter dots, P50 line, P85 line, plus the lead/cycle lens) → mitigated by **shape** disambiguation in the legend (dot vs solid line vs dashed line) per the baseline's D4 rule; this is exactly the non-color channel the spec requires, not a gap. Palette chroma remains deferred (issue-294 Open Question).
- **Same-day / sub-day deliveries collapse to near-zero on a days axis** (a local tool can deliver an issue in hours) → mitigated by returning durations as **fractional days** (`double`, not integer), so the scatter keeps vertical spread and percentile lines are meaningful; display formatted to 1 decimal. Accepted trade-off: the axis label says "days" but values may be fractional — this is conventional for cycle-time control charts.
- **Issue-count rolling window (N=10) is arbitrary** → it is fixed and unit-tested; if a future operator finds it too reactive or too smooth, it's a one-constant change behind the helper. Documented as a non-user-configurable constant.
- **`CompletedAt` null residual** (any pre-backfill done rows not yet covered by `20260629120000_BackfillIssueCompletedAt`) → such issues are excluded from the surface (no completion anchor, no window membership). Mitigated by the idempotent backfill migration; residual risk tracked in Open Questions.
- **EF Core SQLite cannot translate `DateTimeOffset` comparisons on the TEXT `Time` column** (documented at `IssueQuerier.cs:308-314`) → the work-started scan pulls candidate rows and filters in memory, exactly as `GetQualityAsync` already does. Bounded by single-project scale on a local tool; accepted.
- **Scatter density / overlap** at small N → mitigated by modest dot size and opacity; not a correctness risk.
- **DTO change couples server + web releases** → additive surface only; old web never calls the new route, old server simply lacks it (web's `useDeliveryTime` would 404 → error state, not a crash). Safe either direction.

## Migration Plan

1. **Server first (additive, safe):** add `GetDeliveryTimesAsync` to `IssueQuerier`, the `DeliveryTimeMetrics*` DTOs to `IssueRoutes.Dtos.cs`, and the `MapIssueDeliveryTimeMetrics` partial + registration. Existing `/metrics/completion`, `/metrics/quality`, `/metrics/approval-wait` are untouched. Deploy via `mo update server`.
2. **Extend server tests:** in `IssueQuerierSpecs.cs` add `GetDeliveryTimesAsync_*` unit tests (lead = creation→completion, cycle = earliest-work-start→completion, retry keeps earliest start, reopen moves completion anchor keeping earliest start, no-work-start → null cycle, cancelled excluded, 30-day windowing, empty window → empty `Points`); in `IssueMetricsApiSpecs.cs` add the `200`/`404`/empty/shape HTTP specs. Reuse the existing `SeedIssue`/`SeedEvent` helpers; inject `now`.
3. **Web:** add `useDeliveryTime` hook, the `ScatterSeries` kit primitive (+ test), the `model/delivery-time.ts` percentile helper (+ test), and `CycleTimeChart.tsx`; mount in `ProductivityZone.tsx`; add the mock + ordering assertion in `ProductivityZone.test.tsx`; add `useDeliveryTime` to any dashboard-page test that mocks `entities/issue`.
4. **Rollback:** the feature is additive end-to-end. Roll back the web widget to hide the chart; roll back the server route to drop the surface. No schema migration to reverse.

Verification gates: `npm test` (server, C# warnings-as-errors), `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`.

## Open Questions

- **Rolling window size N (D7):** 10 issues is the proposed default. Confirm before implementation; it is a single constant if a different reactivity/smoothness is preferred.
- **Sub-day duration display:** return fractional days (proposed) vs. whole days. Fractional preserves scatter spread but the axis reads "days"; whole days matches the literal "y=周期天数" acceptance text but piles same-day issues at y≈0. Lean: fractional with a 1-decimal display.
- **`CompletedAt` null residual:** confirm the backfill migration covers all historical done issues on the target project before relying on the completion anchor; decide whether null-`CompletedAt` done issues should ever surface (currently: no — they have no completion date to window or plot).
- **Palette chroma:** a third series (P85) plus the lens distinction pushes the grayscale palette; if shape disambiguation proves insufficient in review, introducing chroma into `--chart-*` is the lever (deferred from issue 294, would ripple beyond charts).
