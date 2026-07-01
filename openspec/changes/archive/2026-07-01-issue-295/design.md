## Context

The Dashboard already tells the operator *how long* an issue takes end-to-end (cycle-time scatter), *how much* ships (throughput), *how often* it ships first-time-right (quality), and *how much* it costs — but never *where the time goes*. There is no per-stage (plan / build / check / integrate) duration view, no separation of active work from waiting, and therefore no way to tell whether the bottleneck is a slow stage, an approval gate, or idle gaps. See `proposal.md` for motivation and `specs/` for requirements.

Crucially, **all the data already exists** — this is a pure additive read:

- `StageStarted` / `StageCompleted` workflow-run events (`WorkflowRunEventRow`, source `/mohist/workflow-runs/{runId}`, stage carried in the `Stage` payload, timestamp in the CloudEvent `Time`).
- `IssueWorkStarted` / `IssueWorkCompleted` issue events (source `/mohist/issues/{issueId}`, carrying `workflowRunId`), and the indexed `Issue.CompletedAt` (the terminal `done` moment, already backfilled by migration `20260629120000`).
- Approval `RequestedAt` / `RespondedAt` already projected by `MohistDefaultWorkflowProjection.StageApprovals`.

So the change introduces **no new events, no persisted fields, no domain writes, no schema change, and no new charting dependency**. It is the last piece needed to make flow visible end-to-end.

The implementation sits on two established baselines that must be composed against, not modified:

- **Backend**: the `IssueQuerier` metrics engine and its project-scoped `IssueRoutes` endpoints (`DeliveryTimeMetrics`, `ApprovalMetrics`, `QualityMetrics`, `CompletionMetrics`). The defining constraint here is that **EF Core SQLite cannot translate `DateTimeOffset` against the TEXT columns**, so every existing surface fetches candidate rows unfiltered and aggregates in memory. The new surface follows the same idiom.
- **Frontend**: the hand-rolled SVG dashboard chart kit (`ChartContainer`, `ChartAccessibility`, `ChartAxes`, `ChartLegend`, series primitives, `useReducedMotion`) — **no third-party chart library** — plus the TanStack Query hook pattern under `entities/issue/api/`.

Stakeholders: the operator (consumer of the Dashboard), and the existing metrics surfaces (whose contracts must remain untouched).

## Goals / Non-Goals

**Goals:**

- A project-scoped, windowed, read-only backend surface deriving, purely from already-persisted events: (a) per-stage average + median duration with sample counts; (b) a population-weighted flow-efficiency ratio; (c) a wait breakout (average approval-gate wait + average inactive gap per delivered issue).
- A Dashboard Productivity-zone widget rendering the stage-duration distribution as horizontal bars, with an average/median lens toggle, the flow-efficiency ratio, and the wait breakout alongside.
- A defined empty result distinguishable from a genuine zero, so the chart renders "no data yet" rather than misleading "instant stages" / "100% efficiency".
- Strict additivity: existing endpoint and chart contracts are unchanged; no new charting dependency.

**Non-Goals** (per `proposal.md`):

- Per-issue stage-time drill-down; per-label grouping; budget/threshold alerts.
- Database-side materialization of stage durations (independent tech debt, does not block this read).
- A new charting library, or any change to the `dashboard-charts` baseline's contract.

## Decisions

### D1 — Backend surface placement: new `IssueRoutes.StageDurationMetrics.cs` + `IssueQuerier.GetStageDurationsAsync`

Add a new `GET /api/projects/{projectRef}/issues/metrics/stage-duration` endpoint mapped in `IssueRoutes.MapIssueRoutes` (alongside `MapIssueDeliveryTimeMetrics`), and a new `IssueQuerier.GetStageDurationsAsync(projectId, now)` method. The endpoint injects `TimeProvider` and calls `timeProvider.GetUtcNow()` (matching `DeliveryTimeMetrics` — the newer, no-wall-clock idiom), then builds the response DTO via a private `BuildResponse` helper, wrapped in `ApiResults.Ok`.

**Rationale:** this is exactly where every sibling metric lives. `IssueQuerier` already centralizes project issue-loading, run-discovery, and the in-memory aggregation helpers; duplicating them in a second class would be wasteful.

**Alternatives considered:**
- *A separate `StageDurationQuerier` class.* Rejected — it would have to re-derive issue loading, `IssueRowMapper`, `LoadWorkflowStatesAsync`, and run discovery, all of which `IssueQuerier` owns. Net cost > net clarity.
- *An Orleans grain read.* Rejected — these are derived aggregates over durable events, not entity state; siblings are all `IScopedService` queriers. Consistency wins.

### D2 — Window: fixed 30 days, anchored on `Issue.CompletedAt` (shared with delivery-time)

`windowFrom = now.AddDays(-30); windowTo = now;` and membership keyed on the delivered issue's `CompletedAt` (the terminal `done` moment), exactly as `GetDeliveryTimesAsync` does. Hard-coded, not caller-configurable.

**Rationale:** the spec mandates the stage-duration chart share the *same delivered-issue population* as the cycle-time scatter. Anchoring on completion (not creation/update) and using the identical 30-day `W` guarantees the two charts see the same issues. A 7-day window (as approval-wait uses) would yield too few samples for a four-stage × avg/median breakdown.

**Alternative considered:** *7-day window.* Rejected — sample starvation at the stage level, and breaks population parity with the cycle-time scatter.

### D3 — Run discovery: reuse the quality-metrics pattern across *all* of an issue's workflow runs

An issue may have multiple workflow runs (a `rerun` / `rerun-from-stage` produces additional runs). To gather stage events for the full history we reuse the exact pattern from `GetQualityAsync`:

1. Collect the project's issue sources `/mohist/issues/{issueId}`.
2. Scan durable `IssueEvents`; from `IssueWorkStarted` / `IssueWorkCompleted` payloads read `workflowRunId` (via the existing `ReadWorkflowRunId` which tolerates camel/Pascal casing), accumulating into `runIdsByIssue`.
3. Also add each issue's current `issue.WorkflowRunId`.
4. `Distinct` the union, then load stage events from `WorkflowRunEvents` filtered by those run sources.

**Rationale:** quality metrics already solves "all runs for an issue, including historical ones". Reusing it keeps multi-run semantics consistent across surfaces and is mandated by the spec ("latest attempt is taken across the issue's workflow runs").

**Alternative considered:** *only the current `issue.WorkflowRunId`.* Rejected — it would miss stages executed in prior runs, violating the cross-run latest-attempt requirement.

### D4 — Latest-attempt-per-stage: order events by `(Time, Id)` and take the last `StageStarted` with its following `StageCompleted`, per stage id, across the issue's full event history

For each `(issue, stage)` pair: gather all `StageStarted` / `StageCompleted` events for that stage across the issue's runs, order by `(Time, Id)` (Id is the per-source sequence, breaking ties), take the **last** `StageStarted`, and pair it with the **first** `StageCompleted` that follows it in that order. If no following `StageCompleted` exists, that stage contributes an *undefined* sample (excluded from avg/median, not treated as zero).

**Rationale:** the durable `WorkflowRunEvents` table is append-only, so `(Time, Id)` order yields the true latest attempt. Because a `rerun-from-stage` emits fresh `StageStarted` events for the re-attempted stage *and* resets (then re-emits) downstream stages, taking the latest `StageStarted` per stage id naturally subsumes the invalidate-on-restart semantics without needing stage-definition awareness. This matches the spec ("the most recent `StageStarted` → matching `StageCompleted` pair for that stage") and the invalidate-on-restart idiom under which earlier attempts are superseded, not averaged in.

**Implementation note:** the existing `LoadWorkflowRunEventFactsAsync` projection selects `{ Source, Id, Type, Data }` but **not** `Time`. A stage-duration loader (or an extended projection selecting `Time`) is required — no schema change, `Time` already exists on `WorkflowRunEventRow`. Stage identity is read with the existing `ReadWorkflowEventStage` (camel/Pascal tolerant).

**Alternatives considered:**
- *Reuse `WorkflowEventQuerier.FilterInvalidatedControlEvents` directly.* Rejected — it operates per-run over a single run's transcript with stage-definition awareness, designed for the read/transcript path; for cross-run aggregation a simpler per-`(issue, stage)` latest-pair rule is correct and sufficient.
- *Average/sum all attempts.* Rejected — explicitly forbidden by the spec (earlier invalidated attempts must not contribute).

### D5 — Three-way distinguishability of empty / undefined / genuine-zero

- **Empty** (no delivered issues in the window): `200` with an empty stages array, `null` flow-efficiency ratio, `null` wait-breakout fields, zero sample counts. Distinguishable by the consumer from any genuine value.
- **Undefined** (latest attempt started but never completed): that stage contributes *no* defined sample; excluded from average/median and from the sample count; never coerced to zero.
- **Genuine zero** (same `StageStarted` and `StageCompleted` moment): a real `0` sample, counted, with a non-zero sample count — distinguishable from the empty result.

Nullable `double?` for all aggregate fields (mirrors `ApprovalWaitMetricsResponse`) plus per-stage `SampleCount` make the three states expressible on the wire.

**Rationale:** the spec requires the chart to render "no data yet" rather than "instant stages" or "100% flow efficiency".

### D6 — Cycle decomposition into three non-overlapping components that sum to cycle time

For each delivered issue with a defined, strictly positive cycle time (`earliest IssueWorkStarted` → `CompletedAt`):

- `activeWork = Σ(latest-attempt stage durations) − approvalGateWait`
- `approvalGateWait = Σ(respondedAt − requestedAt)` over completed approvals (`approved`/`rejected`), via `MohistDefaultWorkflowProjection.StageApprovals` (same definition as `approval-waiting-metrics`); pending (`awaiting`) approvals contribute nothing.
- `inactiveGap = cycle − Σ(latest-attempt stage durations)`

The three are non-negative and **sum to cycle by construction** (`activeWork + approvalGateWait + inactiveGap = Σstages − wait + wait + (cycle − Σstages) = cycle`).

**Rationale for subtracting approval wait from active work:** an approval gate sits *inside* a stage's `StageStarted`→`StageCompleted` span. Counting the full stage span as "work" *and* the approval wait as "wait" would double-count and break the non-overlap/sum invariant. The spec defines active work as stage time *net* of approval wait, so the components stay non-overlapping.

**Flow-efficiency ratio** is the population-weighted `Σ activeWork ÷ Σ cycleTime` over issues with defined positive cycle (not the arithmetic mean of per-issue ratios), so a long cycle isn't overweighted by a tiny one.

**Alternative considered:** *define active work as raw `Σ stage durations` without subtracting approval wait.* Rejected — double-counts wait, breaks the sum invariant, and contradicts the spec's non-overlap requirement.

### D7 — Frontend: horizontal bars rendered *locally* in `StageDurationChart`, composing only the baseline wrappers

The chart kit has only **vertical** bar primitives (`BarSeries` / `SegmentedBarSeries` use `scaleY`). The spec requires **horizontal** bars (one per stage). Rather than add a new primitive to the shared kit, the horizontal-bar geometry is rendered locally inside `StageDurationChart` (mirroring how `CycleTimeChart` keeps its `PercentileOverlay` local rather than in the kit), while reusing the baseline wrappers: `ChartContainer` (four-state), `ChartAccessibility` (sr-only summary + non-color-only legend), `ChartAxes`, `ChartLegend`, `useReducedMotion`, theme-token classes (`fill-chart-*`), `tabular-nums`, and **`transform: scaleX(ratio)` with a left `transformOrigin`** for bar motion (never animating `width`/`height`, honoring `prefers-reduced-motion`).

**Rationale:** the proposal mandates "composes against the `dashboard-charts` baseline (no baseline requirement changes)" and "Modified Capabilities: None". There is only one consumer of horizontal bars today; extracting a `HorizontalBarSeries` now would modify the shared baseline for a single caller. The `PercentileOverlay` precedent shows chart-specific geometry is expected to live local to its chart.

**Alternatives considered:**
- *Add a `HorizontalBarSeries` to the shared kit.* Deferred — only one consumer (YAGNI); if a second horizontal-bar chart appears, the local geometry can be extracted mechanically since it already follows every kit convention.
- *Reuse vertical `BarSeries` with a rotated coordinate system.* Rejected — breaks text labels and axis layout; fragile.

### D8 — Average/median lens toggle from a single backend read

The surface returns **both** average and median (plus sample count) per stage. The widget exposes a `LensToggle` (segmented control, `role="group"`, `aria-pressed`) identical in shape to `CycleTimeChart`'s lead/cycle toggle; bar lengths follow the selected lens with no second fetch.

**Rationale:** the spec requires both lenses with no new backend read; exposing both lets the operator spot outlier-skewed stages.

### D9 — Flow-efficiency ratio + wait breakout rendered alongside the bars in one widget

The ratio and the two wait-breakout averages are displayed next to the bars (sourced from the surface, never computed client-side from a partial sample), within the single `StageDurationChart` widget, sharing one query and one three-state wrapper. Pending approvals are not shown as wait here (they surface as attention items elsewhere).

**Rationale:** the spec says "next to the bars"; one widget keeps one query, one loading/error/empty lifecycle, and one accessibility summary.

## Risks / Trade-offs

- **[SQLite in-memory scan cost grows with the project's issue/event history]** → Mitigation: bounded by project scope and the 30-day completion window (identical to the shipping `delivery-time` and `quality` surfaces, which already perform acceptably); aggregation is a single pass over the bounded delivered-issue set; no cross-project scan. DB-side materialization is tracked as separate tech debt (explicitly out of scope).
- **[Existing `LoadWorkflowRunEventFactsAsync` omits the `Time` column]** → Mitigation: add a stage-duration loader (or extend the projection) selecting `Time`; `Time` already exists on `WorkflowRunEventRow`, so no schema change or migration is involved.
- **[Approval-wait double-counting vs. stage span]** → Mitigation: D6 subtracts `approvalGateWait` from `activeWork`, so the three components are non-overlapping and sum to cycle by construction (spec-defined).
- **[Clock-injection inconsistency across endpoints (some use `DateTimeOffset.UtcNow`)]** → Mitigation: the new endpoint injects `TimeProvider` (the cleaner idiom, matching `DeliveryTimeMetrics`); tests inject a fixed `now`, satisfying the no-wall-clock rule.
- **[Local horizontal-bar geometry vs. shared kit]** → Mitigation: follows the `PercentileOverlay` precedent and every kit convention (transform-based motion, theme tokens, reduced-motion), so a later extraction is mechanical; no baseline contract change.
- **[Median even-count formula divergence across surfaces]** → Mitigation: reuse the exact odd/even median formula already in `GetApprovalWaitAsync` so all surfaces agree.
- **[Web lands before/after server]** → Mitigation: the widget routes fetch failure through the existing three-state `error` shell; no breaking change either way, and the two are independent additive reads.

## Migration Plan

- **Purely additive**: one new endpoint + one new widget. No schema migration, no data backfill (the read consumes events the system already records; `Issue.CompletedAt` was already backfilled by migration `20260629120000`).
- **Deploy**: ship server and web together (preferred). If web lands first, the new query simply resolves to the `error` state until the endpoint exists — no regression to any existing surface.
- **Rollback**: revert the endpoint file, querier method, hook, and widget. Nothing else depends on them; removing them leaves all existing metrics surfaces and charts intact. No data cleanup is required.

## Open Questions

None blocking. Two low-stakes items deferred to implementation:
- **Bar unit/format** (hours vs minutes, tick formatting) — follow `tabular-nums` and the existing `formatAxisValue` convention; hours is the natural unit for stage durations. No spec impact.
- **Whether the wait breakout should later become per-stage** (which stage's approval is the bottleneck) — out of scope per Non-Goals (no per-issue drill-down); the population-level breakout satisfies the spec.
