## Context

The Dashboard surfaces spend as two isolated scalars (cumulative `totalCost` and `todayCost`) via `InvestmentPanel`, which reads `/api/projects/{projectRef}/agent/cost`. The operator cannot see whether daily spend is trending up, or whether cost-per-ship is improving as output scales.

Relevant current state (verified in code):

- **The daily cost data already exists on the server.** `/api/projects/{projectRef}/agent/usage` returns a 7-day daily-bucket timeseries (`AgentUsageTimeseriesDto` with `Buckets[]` of `UsageBucketDto`, each carrying `CostAmount`). Route: `packages/server/src/Mohist.Server/Api/AgentRoutes.cs:40`. Computation: `AgentSessionQuerier.GetUsageTimeseriesAsync` (`AgentSessionQuerier.cs:456-494`). It is fully tested (`AgentUsageTimeseriesApiSpecs.cs`) but **not consumed by any web code today**.
- **The cost-per-ship rollup exists** (`/agent/cost`, `BuildCostPerShip` in `AgentRoutes.cs:64-69`) but only as a single lifetime scalar — **no per-day cumulative ratio exists**. This is the one real backend gap.
- **Issue completion time is persisted**: `Issue.CompletedAt` (`Issue.cs:15`), set on `Complete`/`Close` transitions (`Issue.Transitions.cs:171-212`), backfilled by an idempotent migration dated today (`20260629120000_BackfillIssueCompletedAt.cs`). So per-day cumulative shipped counts are derivable without new collection.
- **No chart library is installed** (`packages/web/package.json` has only `@xyflow/react` for node graphs). The only chart precedent is a hand-rolled inline SVG sparkline in `CompletionTrend.tsx:55-61` using `role="img"` + `aria-label`.
- **Theme tokens for charts already exist** as `--chart-1..5` mapped to Tailwind `text-chart-*`/`fill-chart-*`/`bg-chart-*` (`packages/web/src/app/styles/index.css:20-24`). They are currently **grayscale** (zero chroma oklch) in both `:root` and `.dark`.
- **No `prefers-reduced-motion` handling exists anywhere** in the web package; `tests/setup.ts:9-23` stubs `matchMedia` to always return `matches: false`.
- Productivity widgets today render **no loading state** — they degrade to empty until `data` resolves. Empty-state shell convention: `<section data-testid="productivity-<name>" data-state="empty" aria-label="…">`.

Constraints / stakeholders:

- This is the **first chart on the Dashboard**, so it must also establish the reusable chart baseline (library, tokens, three states, a11y) that later chart issues compose against.
- Per `design/architecture.md`: Web UI only renders state and emits user intent; authoritative state and all interpretation live in the Server. This widget is read-only.
- Per `design/web-ui.md`: query hooks own fetching; components render state. Prefer dense, scannable screens.

## Goals / Non-Goals

**Goals:**
- Mount a daily cost bar chart (one bar per trailing day) in the Productivity zone, sourced from the existing `/agent/usage` timeseries — no new data collection.
- Overlay a cost-per-ship trend line (cumulative spend ÷ cumulative shipped-issue count, as of each day), sourced from a new additive per-day cumulative series on the server.
- Establish the reusable Dashboard chart baseline: one pinned rendering approach; theme-token colors; a shared loading/error/empty wrapper with a next-action empty state; an a11y wrapper (SR data summary + non-color legend); `tabular-nums`; transform-based motion honoring `prefers-reduced-motion`.
- Full loading/error/empty states for the cost-trend widget via the shared wrapper.
- Preserve every existing contract (`/agent/cost` rollup, the `Buckets[]` timeseries shape) unchanged; the cumulative series is strictly additive.

**Non-Goals** (per issue/proposal):
- No per-session or per-issue cost breakdown, no budget alerts, no multi-currency.
- No change to `InvestmentPanel` scalar figures or their presentation.
- No new events, domain writes, or data collection.
- No chroma overhaul of the `--chart-*` palette (deferred — see Open Questions).

## Decisions

### D1. Pin a first-party SVG chart kit as the single "chart library"; add no external dependency.

The spec requires "a single, project-wide pinned chart library" with a retire-to-swap rule. We interpret "library" as **the pinned chart rendering approach**, and pin a first-party SVG chart kit (a small set of internal primitives: `<BarSeries>`, `<LineSeries>`, `<ChartAxes>`, `<ChartLegend>`) over adding recharts/visx/chart.js.

Rationale:
- Scope is tiny (7 bars + 1 trend line). The only existing chart (`CompletionTrend.tsx`) is already hand-rolled SVG; this continues that precedent rather than splitting the dashboard across two rendering approaches.
- The acceptance criteria demand fine-grained control that external libs fight: colors strictly from our CSS-variable tokens, bar-height motion via `transform` only, `prefers-reduced-motion` suppression, a screen-reader data summary, and a shape-based legend. First-party SVG makes all of these direct.
- Zero bundle/runtime dependency; no version drift; no second library to retire later.

Alternatives considered:
- **recharts/tremor**: would satisfy "library" literally but ships its own theming layer and default motion; forcing it onto our tokens + reduced-motion + custom a11y summary yields more friction than the 7-bar use case justifies. If a future chart genuinely needs heavy interactivity (brushing, zoom, complex stacking), the spec's retire rule permits swapping — swap the whole dashboard, not add alongside.
- **visx/d3**: lower-level than recharts but still a dependency for what is a `<rect>` + `<path>` plot.

The retire rule is recorded as: future dashboard charts compose against this kit; introducing an external charting package requires retiring this kit dashboard-wide, not running it alongside.

### D2. Baseline lives under `packages/web/src/pages/dashboard/charts/`; the cost-trend widget consumes it.

Placement:
- `pages/dashboard/charts/` — the reusable baseline: `ChartContainer` (three-state wrapper), `ChartAccessibility` (SR summary + non-color legend), motion/`useReducedMotion` hook, and the SVG series primitives. Dashboard-scoped because the spec scopes the baseline to "dashboard charts."
- `pages/dashboard/productivity/CostTrendChart.tsx` — the widget, mounted in `ProductivityZone.tsx` (after `InvestmentPanel`).
- `entities/agent/api/agent-usage.ts` — new `fetchAgentUsage` + `useAgentUsage` hook, mirroring `entities/agent/api/cost-rollup.ts` (same `request<T>` fetcher, `projectApiPath`, `staleTime: 60_000`, query key `['agent', 'usage', projectId]`), re-exported from `entities/agent/index.ts`.

This follows the existing placement rule (query hooks in `entities/<bounded-concept>/api/`, widgets in `pages/dashboard/productivity/`).

### D3. Three-state wrapper owns loading/error/empty; empty state carries a concrete next action.

`ChartContainer` accepts `{ status: 'loading' | 'error' | 'empty' | 'resolved', emptyAction, children }` and renders only one branch. It uses `role="status"`/`aria-live="polite"` for loading and error (consistent with `DashboardDigestWidget.tsx:29-42`), and the empty branch renders the caller-supplied next-action string (for cost-trend: "Cost and cost-per-ship appear once an agent session reports usage on this project"). This retires the current "degrade-to-empty-while-loading" pattern **for charts only** — `InvestmentPanel` and other scalar widgets are untouched.

### D4. A11y wrapper: `role="img"` short label + `sr-only` full data summary + shape-disambiguated legend.

Each chart composes `ChartAccessibility`, which renders:
- `role="img"` + concise `aria-label` on the SVG (series + time range), following `CompletionTrend.tsx:55-61`.
- A `sr-only` textual data summary (series names, window, and salient values: total window cost, peak day, first/last cost-per-ship).
- A visible legend where series are disambiguated by **shape**, not color alone: bars = a filled rect swatch; trend line = a line-with-marker swatch. This is what makes the current grayscale palette acceptable (see D6).

### D5. Server: add an additive per-day cumulative series to `/agent/usage`; compute via prefix sums; preserve null-vs-zero semantics.

The one backend change. Extend `AgentUsageTimeseriesDto` with a sibling field (strictly additive — `Buckets[]` shape and `/agent/cost` are untouched):

```
IReadOnlyList<CumulativeCostPerShipPointDto> CumulativeCostPerShip
// aligned 1:1 with Buckets[] by day (same window, same length)
record CumulativeCostPerShipPointDto(
    DateTime DayEnd,
    double? CumulativeCost,        // null when no session with usage exists up to this day (matches totalCost nullability)
    string? Currency,
    int CumulativeShippedCount,
    double? CostPerShip)           // null = undefined (shipped==0); 0 = genuine free shipping
```

Computation in a new `AgentSessionQuerier` method, co-located with `GetUsageTimeseriesAsync`:

- `preWindowSpend` = sum of `UsageSummary.CostAmount` over all project sessions with usage whose `CreatedAt < rangeFrom` (one extra sum query, lifetime-bounded).
- `preWindowShipped` = count of done issues whose `CompletedAt < rangeFrom` (via `IssueQuerier`, same source the `/cost` handler already uses).
- Walk the 7 window days: `cumulativeCost[i] = preWindowSpend + Σ(bucketCost[0..i])`; `cumulativeShipped[i] = preWindowShipped + count(done issues completed on days [0..i])`; `costPerShip[i] = shipped[i] > 0 ? cumulativeCost[i] / shipped[i] : null`.

This is the efficient variant of the naive "load every session + every issue and prefix-sum" (considered; rejected as more data movement than needed). Both are pure reads over already-recorded data — no new events, writes, or collection, satisfying `agent-cost-metrics/spec.md`.

**Null-vs-zero is the subtle requirement** (`agent-cost-metrics/spec.md` scenarios "no shipped issues yet" and "genuine zero"). Modeled by `double? CostPerShip`: `null` = undefined (shipped 0); `0` = free shipping (cost 0, shipped > 0). System.Text.Json serializes `double?` `null` and `0` distinctly by default; the web side skips `null` points and plots genuine `0` at value 0.

### D6. Colors strictly from `--chart-*` tokens; dual y-axis because the two series have different units.

- Bar fill = `fill-chart-2`, trend stroke = `stroke-chart-5` + marker shape; axes/grid/labels from `text-muted-foreground`/`border-border`. No hex/rgb/named literals on the chart surface (retire-on-chart-surface rule).
- **Dual y-axis**: left axis = daily cost (currency), right axis = cost-per-ship (currency/issue). The issue explicitly asks for an *overlay* ("柱图上叠一条趋势线"), but the two series have different units, so plotting them on one axis would mislead. Dual axes with clearly labeled, token-colored ticks is the standard overlay form.

Alternatives considered for the second axis: small multiples (rejected — loses the overlay the user asked for); index-normalized (rejected — hides absolute $ values that matter to the user voice).

### D7. Motion: bars grow via `transform: scaleY()` (`transform-origin: bottom`); reduced-motion suppresses it.

`useReducedMotion()` reads `matchMedia('(prefers-reduced-motion: reduce)')`. When not reduced, bars enter/update via `transform`/`opacity` only — never `width`/`height`/layout. When reduced, final values render with no animation. `tests/setup.ts` matchMedia stub is extended to let individual tests opt into the reduce case. Numeric labels (axes, data labels, tooltips, legend) use `tabular-nums`.

## Risks / Trade-offs

- **Grayscale `--chart-*` palette gives weak bar/line color contrast** → mitigated by D4's shape-based legend (bars vs line+marker), which is exactly the non-color disambiguation the spec requires, not a gap. Introducing chroma is deferred (Open Question).
- **Dual y-axis can mislead casual readers** → mitigated by clearly labeling both axes, token-coloring each axis to match its series, and ordering the legend to match axis assignment. Accepted trade-off for the requested overlay form.
- **Cumulative-series computation loads pre-window sessions + all done issues** → bounded by single-project size on a local tool; pure read; prefix-sum form keeps it to two extra queries. Monitor if project history grows large.
- **Cumulative series is index-aligned to `Buckets[]`** → fragile if the two arrays drift. Mitigated by deriving both from the same window definition in one method and asserting equal length in tests.
- **Done issues with `null CompletedAt`** (any pre-backfill rows not yet migrated) would be excluded from per-day cumulative counts → mitigated by the idempotent `20260629120000_BackfillIssueCompletedAt` migration; residual risk tracked in Open Questions.
- **DTO change couples server + web releases** → additive field; old web ignores it, old server omits it (web treats missing as "no trend"). Safe either direction.
- **`prefers-reduced-motion` handling is new to the codebase** → test stub must be updated; risk of missing a motion path. Mitigated by a dedicated reduced-motion test asserting no `transform` animation is applied.

## Migration Plan

1. **Server first (additive, safe):** add `CumulativeCostPerShip` to `AgentUsageTimeseriesDto` + the new querier method. Existing `/agent/cost`, `/agent/usage` `Buckets[]`, and all rollup fields are unchanged. Deploy via `mo update server`.
2. **Extend server tests** in `AgentUsageTimeseriesApiSpecs.cs`: cumulative-series math, the undefined-vs-zero scenarios, zero-sample `200` case, and the 404 case (all already specified in `agent-cost-metrics/spec.md`).
3. **Web:** add `useAgentUsage`, the `pages/dashboard/charts/` baseline, and `CostTrendChart`; mount in `ProductivityZone`. Update `tests/setup.ts` matchMedia stub. Deploy via `mo update` (server already has the field).
4. **Rollback:** the feature is additive end-to-end. Roll back the web widget alone to hide the chart; roll back the server field to drop the trend (bars still render from the unchanged `Buckets[]`). No schema migration to reverse.

Verification gates: `npm test` (server, C# warnings-as-errors), `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`.

## Open Questions

- **Chart palette chroma**: do we introduce real color into `--chart-1..5` now (for stronger bar/line separation), or keep grayscale and rely on shape disambiguation? Proposal currently defers — `InvestmentPanel` color changes are explicitly out of scope, and a palette change ripples beyond charts. Lean: defer; revisit when a second chart lands.
- **Cumulative series field placement**: sibling array on `/agent/usage` DTO (chosen) vs. a separate `/agent/cost-trend` route. Spec says "co-located with the agent-usage surface" and "strictly additive"; the sibling array satisfies both most directly. Confirm before implementation.
- **`CompletedAt` null residual**: confirm the backfill migration covers all historical done issues on the target project before relying on per-day cumulative shipped counts; decide whether null-`CompletedAt` done issues should count toward `preWindowShipped` (currently: no, since they have no day to bucket into).
