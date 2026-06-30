## Context

The Dashboard already exposes a weekly completion sparkline (`CompletionTrend.tsx:1-132`) and, via issue 294, a daily cost bar chart with a trend overlay (`CostTrendChart.tsx:1-246`). The operator still cannot see the *daily* delivery rhythm — whether ships are steady, accelerating, or decaying, and how many daily terminations are failures.

The data already exists. The server endpoint `GET /api/projects/{ref}/issues/metrics/completion?bucket=day` returns **dense trailing-30-day buckets** carrying per-bucket `Completed` and `Failed`, bucketed by **terminal-event time** (`IssueWorkCompleted` / `IssueClosed`), not by `updatedAt` (`IssueQuerier.cs:245-366`, `IssueRoutes.Metrics.cs:12-46`; covered by `IssueMetricsApiSpecs.cs:34-52` and `IssueQuerierSpecs.cs:777-865`). So this change is **pure additive read on the web**: no server change, no new event, no new collection path. It composes against the chart baseline issue 294 established.

Three facts in the current code shape the design:

1. **`BarSeries` cannot stack.** `BarDatum.value` is a scalar and each bar is one full-height `<rect>` scaled by `transform: scaleY()` from a bottom origin (`BarSeries.tsx:3-6, 43-59`). Completed+failed cannot be expressed with the existing primitive.
2. **The completion hook is week-only.** `fetchCompletionTrend` / `useCompletionTrend` hardcode `bucket=week` in both the URL and the query key (`completion-trend.ts:19,26`); the hook's tests pin that exact contract (`completion-trend.test.ts:24-79`). No client calls `bucket=day` today.
3. **No client-side averaging exists.** `CostTrendChart`'s "trend line" is the server-computed `cumulativeCostPerShip` (`AgentSessionQuerier.cs:841`), mapped 1:1 on the client (`CostTrendChart.tsx:112-125`). The 7-day moving average will be the first client-side averaging code in the repo.

Stakeholders: the operator (reads delivery health), the dashboard chart baseline (must stay consistent), and the server completion-metrics contract (consumed unchanged).

## Goals / Non-Goals

**Goals:**
- Mount a daily delivery throughput widget in the Dashboard `Productivity` zone as a sibling to `CompletionTrend` and `CostTrendChart`.
- Render one bar per trailing day (fixed 30), each encoding that day's `Completed`, with a darker overlaid `Failed` segment on the same count axis, sourced from the existing `bucket=day` endpoint.
- Overlay a 7-day moving average of the completed series, computed client-side.
- Route loading / error / empty through the shared three-state wrapper with a concrete next-action empty state.
- Compose entirely against the existing chart baseline — no new charting library, theme-token colors, accessibility wrapper, `tabular-nums`, transform-based reduced-motion-aware animation.
- Stay purely read-only: no domain write, no new backend contract.

**Non-Goals** (per proposal/spec):
- No configurable time window (fixed 30 days).
- No bar-click drill-down.
- No weekly/monthly toggle.
- No server changes, no migration of in-memory aggregation to DB-side aggregation (independent tech debt).

## Decisions

### D1 — Stacking semantics: independent overlay on a shared count axis, NOT cumulative stacking

`Completed` (`IssueWorkCompleted`) and `Failed` (`IssueClosed` → `cancelled`) are **disjoint terminal events** in the server's bucketing (`IssueQuerier.cs:339-359`). The spec's scenarios resolve how they combine on screen:

- "each bar's height SHALL encode that day's completed-issue count" (spec §1)
- "A day with no completions renders a zero-height bar rather than a gap" (spec §1) — a day with `completed=0, failed=3` still must render the failed segment, which is impossible under cumulative stacking (total = c+f would be 3, not zero-height).
- "The moving average SHALL be computed over the completed counts … not the failed segment" (spec §2).

**Decision:** single shared "issues per day" axis. For each day, render a light bottom-anchored rect proportional to `completed` and a darker bottom-anchored rect proportional to `failed` at the same x; the MA(7) line is plotted over `completed` on the same axis. Axis max = `max over days of max(completed, failed)` so no segment is clipped or capped. When `failed <= completed` the dark segment reads as a two-tone base; when `failed > completed` it protrudes above the completed rect, preserving the raw failed count and remaining consistent with the "zero-completed day still shows failures" scenario.

**Consequence — single axis, not dual.** Unlike `CostTrendChart` (which needs dual axes because cost-$ and $/issue are different units, `CostTrendChart.tsx:127-128`), here completed, failed, and MA are all issue-counts → **one left axis only**. This is a deliberate simplification of the baseline.

**Alternatives considered:**
- *Cumulative stack (total = completed + failed, failed cap on top).* Rejected: contradicts the "zero-completed -> zero-height bar" scenario and forces the MA (over completed) to sit below the bar tops on a total-axis, conflating two meanings.
- *Capped overlay (failed limited to completed height).* Rejected: under-reports failures whenever `failed > completed` and violates the requirement that the failed segment encode the raw failed count.
- *Dual axis (counts vs counts).* Rejected: same unit, adds noise.

### D2 — Render the two-segment bar via a new focused chart primitive, leaving `BarSeries` untouched

**Decision:** add a `SegmentedBarSeries` primitive to the chart kit (`pages/dashboard/charts/`) that accepts per-bar segments (here: two — completed, failed), scales all segments to one caller-supplied `max`, and renders each segment as a bottom-anchored `<rect>` reusing the exact `transform: scaleY()` / `transformOrigin` / `useReducedMotion()` motion pattern from `BarSeries.tsx:52-59`.

**Rationale:** keeps `ThroughputChart` declarative (mirrors `CostTrendChart`'s `<BarSeries/>` + `<LineSeries/>` composition at `CostTrendChart.tsx:224-243`), is independently unit-testable like the other primitives, and **does not perturb the shared `BarSeries` used by the baseline `CostTrendChart`** (zero regression risk).

**Alternatives considered:**
- *Generalize `BarSeries` to accept optional segments.* Rejected: touches a primitive the baseline depends on, expanding its contract and its 476-line test surface for one consumer.
- *Inline the rects directly in `ThroughputChart`.* Rejected: duplicates the motion/reduced-motion logic and is harder to test in isolation.

### D3 — Daily data via a dedicated daily hook; keep the weekly contract untouched

**Decision:** parameterize the fetch by bucket and add a daily hook alongside the weekly one, leaving `useCompletionTrend`'s signature and query key unchanged:

- Refactor `fetchCompletionTrend(projectId)` → `fetchCompletionTrend(projectId, bucket = 'week')` (thread `bucket` into both the URL and the query path). The weekly hook continues to call it with `'week'`, so its query key stays `['issues','metrics','completion','week', projectId]` and `completion-trend.test.ts:24-79` keeps passing.
- Add `useCompletionThroughput()` → query key `['issues','metrics','completion','day', projectId]`, calling `fetchCompletionTrend(projectId, 'day')`, same `staleTime: 60_000` and `enabled: !!projectId`.
- Reuse the existing bucket-agnostic types `CompletionBucketPoint` / `CompletionTrendResponse` (`completion-trend.ts:5-15`); no new DTO.

**Alternatives considered:**
- *Fully separate `fetchCompletionThroughput` function.* Rejected: duplicates the one-line `request(...)` wrapper for no benefit.
- *One generic `useCompletionTrend(bucket)` hook replacing both.* Rejected: couples two independent widgets' tests and forces a new mandatory argument on the weekly caller.

### D4 — 7-day moving average as a pure, unit-tested helper

**Decision:** put the SMA in a small pure module (e.g. `productivity/model/throughput.ts`) exporting `computeMovingAverage(values: number[], window: number): number[]`. Algorithm — trailing, inclusive of the current day, partial window at the leading edge (spec §2 "fewer than six predecessors still plot"):

```
sma[i] = sum(values[max(0, i-window+1) .. i]) / (i - max(0, i-window+1) + 1)
```

Input is the dense `completed: number[]` (server guarantees 30 buckets, zeros included — `IssueQuerier.cs:289-294`), so **no `null` handling is needed** and the resulting `LineSeries` is a single continuous segment (no gaps, unlike `CostTrendChart`'s nullable cost-per-ship). Call with `window = 7`.

**Rationale for a dedicated module (vs. top-of-file helpers like `CostTrendChart`'s `niceCeil`/`computeTicks`):** the spec pins three MA-specific edge scenarios (partial leading window, over-completed-only, never bridged). A pure helper makes those directly unit-testable without rendering a chart.

**Alternatives considered:**
- *Inline in `ThroughputChart`.* Rejected: harder to unit-test the edge cases.
- *Server-computed MA.* Rejected: violates the spec ("SHALL be derived client-side", spec §2) and the read-only/no-server-change goal.

### D5 — Color tokens follow baseline convention; legend disambiguates by label + shape

**Decision (tokens):** mirror the baseline where primary series = `chart-2` and overlay = `chart-5` (`CostTrendChart.tsx:151,153`):
- completed bar: `fill-chart-2`
- failed segment: `fill-chart-4` (next darker unused token; `chart-3`/`chart-4` are currently unused by any chart)
- MA line: `stroke-chart-5` + `fill-chart-5` markers (consistent with `CostTrendChart.tsx:240-241`)

**Decision (legend / accessibility):** three entries — `{ Completed, shape: bar, fill-chart-2 }`, `{ Failed, shape: bar, fill-chart-4 }`, `{ 7-day average, shape: line, stroke-chart-5 }`. `ChartLegend` auto-renders for ≥2 entries (`ChartLegend.tsx:57`). The non-color channels are **label** (disambiguates the two same-shape bar swatches) and **shape** (line vs bar), satisfying spec §5 "a channel other than color." The SR summary string names the window, peak day, and total/average completed (pattern from `CostTrendChart.tsx:142-148`).

**Alternatives considered:**
- *Use `chart-3` for failed.* Rejected: too close to `chart-2`, weaker separation for the stacked segment.
- *Reinforce completed-vs-failed with a hatch pattern.* Deferred (see Open Questions) — label already satisfies the SHALL.

### D6 — Empty state = no terminal events in the window

**Decision:** empty iff `buckets.every(b => b.completed === 0 && b.failed === 0)` (mirrors `CostTrendChart`'s `hasUsageData` heuristic at `CostTrendChart.tsx:57-73`). A project with failure-only throughput is **not** empty — its failed bars render. Empty-state copy uses the spec-mandated phrasing: "Throughput appears once an issue completes on this project." Routed through `ChartContainer` (`status="empty"` + `emptyAction`), never a bare axis.

### D7 — Mount placement

**Decision:** render `<ThroughputChart/>` in `ProductivityZone.tsx` directly after `<CompletionTrend/>` — thematic grouping escalates weekly completion (sparkline) → daily throughput (full chart). Update `ProductivityZone.test.tsx` to mock + assert containment (the existing pattern at `ProductivityZone.test.tsx:37-51`).

**Alternative considered:** cluster beside `CostTrendChart` (two daily bar charts together). Rejected in favor of completion-health grouping; low-stakes, easy to revisit.

## Risks / Trade-offs

- **`BarSeries` cannot express the failed segment** → Mitigation: new `SegmentedBarSeries` primitive; the shared `BarSeries` and `CostTrendChart` are untouched, so baseline regression risk is isolated to the new primitive's own tests.
- **First client-side averaging code; edge cases are spec-pinned** → Mitigation: pure `computeMovingAverage` helper with dedicated unit tests (partial leading window, all-zero, `window > length`, single element).
- **Stacking semantics had a plausible cumulative reading** → Mitigation: D1 resolves it from the spec's own "zero-completed → zero-height bar" scenario; documented explicitly so reviewers can challenge.
- **Two same-shape legend swatches (completed/failed) differ only by tone + label** → Mitigation: label is the non-color channel (spec-compliant); pattern reinforcement is an explicit future option (Open Questions).
- **Empty heuristic is window-scoped** → a project whose completions all fall outside the trailing 30 days shows the empty state with "once an issue completes…" copy. Mitigation: matches `CostTrendChart`'s window-scoped behavior; copy is guidance not a guarantee; rare for an active project.
- **Refactoring `fetchCompletionTrend` to take `bucket`** touches a tested function → Mitigation: default `'week'` preserves the existing call sites and the weekly hook's pinned query key/URL (`completion-trend.test.ts:24-79` stays green).

## Migration Plan

This is a purely additive, read-only web change. No server, API, event, persistence, or data-migration change is involved.

**Deploy:**
1. Add `SegmentedBarSeries` primitive (+ primitive tests).
2. Refactor `fetchCompletionTrend(projectId, bucket)` with default `'week'`; add `useCompletionThroughput` (+ hook tests for the daily key/URL and dense-30 decode).
3. Add `computeMovingAverage` helper (+ unit tests).
4. Add `ThroughputChart` widget (+ widget tests mirroring `CostTrendChart.test.tsx`).
5. Mount in `ProductivityZone` (+ zone test).

**Rollback:** remove the `<ThroughputChart/>` line from `ProductivityZone.tsx`. The widget, hook, helper, and primitive are all opt-in/additive; nothing else depends on them. The `fetchCompletionTrend` signature change is backward-compatible (default arg), so rollback does not require reverting it. No feature flag is needed for an additive read-only chart.

**Verification:** `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` must pass; the server test suite is unaffected.

## Open Questions

- **MA visual treatment:** should the MA line render with markers at every day (denser, 30 markers) or only at interval/none (cleaner)? `CostTrendChart` marks every point (`CostTrendChart.tsx:70-80`); for 30 daily points this may look busy. Default: follow baseline (mark every point), revisit if visually noisy.
- **Completed-vs-failed accessibility:** label disambiguates today; should we add a hatch/diagonal pattern to the failed segment for stronger low-vision separation? Defer until low-vision feedback.
- **Failure-only projects:** confirm with product that a project with failures but zero completions in-window should show failed bars (current D6) rather than the empty state.
- **Placement:** D7 groups under completion health; if the dashboard read better with the two daily bar charts clustered, the mount line is a one-line move.
