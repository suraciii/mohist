## Context

Insights M2 (#323) relocated every trend chart onto `/insights` and annotated each with its current window badge. But every endpoint is still compiled against a hard-coded window — an operator cannot replay a different period. M3 introduces **one** global `7d / 30d / 90d` selector on `/insights` that re-bases the Signal Summary and all eight chart hooks in a single action.

Today's windows are scattered as literals inside three queriers:

| Surface | Querier / method | Today's fixed window | Previous window | Anchor |
|---|---|---|---|---|
| completion (`bucket=day`) | `IssueQuerier.GetCompletionBucketsAsync` | 30 UTC days | 30 days | terminal-event `Time` |
| completion (`bucket=week`) | same | 12 ISO weeks | 12 weeks | terminal-event `Time` |
| delivery-time | `IssueQuerier.GetDeliveryTimesAsync` | 30 days | 30 days | `CompletedAt` |
| stage-duration | `IssueQuerier.GetStageDurationsAsync` | 30 days | — | `CompletedAt` |
| quality primary (`Window30d`) | `IssueQuerier.GetQualityAsync` | 30 days | 30 days | ship time |
| quality short lens (`Window7d`) | same | **7 days (fixed)** | — | ship time |
| quality trend | same | 30 daily buckets | — | ship day |
| cumulative-flow | `CumulativeFlowQuerier.GetAsync` | **90 days (D6 contract: "not configurable")** | — | snapshot `Day` |
| approval-wait | `IssueQuerier.GetApprovalWaitAsync` | 7 days | — | `respondedAt` |
| agent cost (windowed) | `AgentSessionQuerier.GetCostWindowedAsync` | 30 days | 30 days | session `CreatedAt` |
| agent usage | `AgentSessionQuerier.GetUsageTimeseriesAsync` | 7 days / 7 daily buckets | — | session `CreatedAt` |

Route binding today binds query params straight onto handler signatures (e.g. `string? bucket` in `IssueRoutes.Metrics.cs`); unknown values return 400 via `ApiResults.BadRequest`. Each web hook follows the `fetchX` / `xQueryKey(projectId, …)` / `useX(…)` trio with tuple `queryKey`s; `useCompletionTrend`/`useCompletionThroughput` already fold a dimension (`'day'`/`'week'`) into the key, proving the queryKey-isolation pattern.

Constraints / stakeholders:
- **Shared hooks have non-Insights consumers.** `useCostRollup` feeds the Dashboard `FactoryStatusHeadline`; `useApprovalWait` feeds the Dashboard `AttentionHero`. Per the non-goal (no selector off-Insights), those callers keep invoking **without** a range, so the omit-range branch reproducing today's window is a hard back-compat contract, not a nicety.
- **`approval-wait` is parameterized server-side for contract uniformity** (it is one of the six issue-metrics endpoints) but is **not** one of the eight Insights hooks — no Insights chart consumes it. Its only web consumer is the Dashboard, which calls without a range.
- The CFD window is today a **design D6 contract** (`CumulativeFlowQuerier.TrailingWindowDays = 90`; doc-comments + `IssueMetricsApiSpecs` assert "fixed, not user-configurable"). M3 formally **reverses** that contract.
- EF Core SQLite cannot translate `DateTimeOffset` against the TEXT `Time`/`State` columns, so every windowed method already filters in memory after a bounded fetch — scaling a window is a local literal change, not a query-translation risk.
- No-wall-clock rule: `IssueQuerier` methods take an injected `now`; `AgentSessionQuerier` uses `TimeProvider`. (`GetApprovalWaitAsync`'s route still binds `DateTimeOffset.UtcNow` — a pre-existing wart; the range change keeps the existing clock source per endpoint rather than expanding scope.)

See `proposal.md` for motivation and `specs/{insights-time-range,issue-metrics-time-range,agent-cost-time-range}` for formal requirements.

## Goals / Non-Goals

**Goals:**
- One global `7d / 30d / 90d` selector on `/insights`, default `30d`, owned as page state by `InsightsPage` and propagated to the Signal Summary + all eight chart hooks.
- Server: a uniform `range` query parameter on the six issue-metrics endpoints + the two agent endpoints, where the range drives the current window (and the previous comparison window where one exists).
- Per-hook `queryKey` isolation so a range switch fetches fresh data and never serves another range's cache.
- Back-compat: omitting `range` reproduces **each endpoint's exact fixed window today**, so the Dashboard consumers of the shared hooks are unaffected.
- Formally supersede the CFD D6 "fixed, not configurable" contract (default 90d preserves today) and resolve the quality double-window DTO in-place.

**Non-Goals** (from issue/proposal/specs):
- No custom from/to date picker — only the three presets.
- No new chart, metric, or response **field**; DTOs gain no new property (quality `Window7d`/`Window30d` slots are reused, not renamed/added).
- No selector on any non-Insights page (Dashboard keeps calling shared hooks without a range).
- No change to all-time agent figures (`totalCost`, `todayCost`, all-time `costPerShip`).
- `EpicProgressList` (`useEpics`, a live snapshot) is exempt from the selector.

## Decisions

### D1 — One shared `MetricsRange` value + per-endpoint default; queriers take `int? windowDays`, never the string

Introduce a single small server type (e.g. `Mohist.Server.Metrics.MetricsRange`) that owns the wire vocabulary and the day-count mapping:

- Accepts exactly `"7d" | "30d" | "90d"`; maps to `7 / 30 / 90`.
- `null`/omitted ⇒ `null` day-count ⇒ **the caller's own fixed default** (back-compat). This is the crucial asymmetry: the range→days map is global, but the *fallback default* is **per-endpoint** (completion-day=30, completion-week=12w, delivery=30, stage=30, quality-primary=30, cfd=90, approval-wait=7, cost-windowed=30, usage=7). The validation (unknown non-null value ⇒ 400) lives in the routes, beside the existing `bucket` validation.
- Querier methods gain an `int? windowDays` parameter (null = today's fixed window) — they never see the `"7d"` string, so the Issue and Agent queriers stay decoupled from the selector vocabulary and independently testable.

So `GetDeliveryTimesAsync(projectId, now, windowDays)` computes `windowFrom = now.AddDays(-(windowDays ?? 30))`, and the previous window uses the same length. Every other method follows the same one-line substitution of its current literal.

**Alternatives considered:**
- *Pass the `"7d"` string into the queriers.* Couples both queriers (and their unit tests) to the wire vocabulary, and forces every querier to re-implement parse+default. Rejected.
- *A single global default day-count (e.g. always 30) on omit.* Breaks back-compat for cumulative-flow (90), approval-wait (7), usage (7), and the completion week bucket (12w). Rejected — the AC makes per-endpoint back-compat a hard contract.

### D2 — The CFD D6 contract is reversed in-place; `TrailingWindowDays` becomes the omit-default

`CumulativeFlowQuerier.GetAsync` gains `int? windowDays = null`. When supplied, the window spans that many days; when null, it falls back to `TrailingWindowDays` (90), byte-for-byte preserving today's behavior. The snapshot read path, the `Day`-string filter, and the `CumulativeFlowResult` DTO are **unchanged** — only the two `AddDays(-(TrailingWindowDays - 1))` literals become `-(windowDays ?? TrailingWindowDays)`-derived. The doc-comments and the "not user-configurable" language are rewritten to state the window is range-driven with a 90d default; the `IssueMetricsApiSpecs` CFD assertions are updated to (a) keep asserting the 90d window on omit, and (b) assert a `range=30d` request returns a 30-day series. The DTO gains **no** new field — the existing `rangeFrom`/`rangeTo` already carry the actual bounds the badge derives from.

**Alternatives considered:**
- *Keep D6 and exclude CFD from the selector.* Rejected — the proposal and the `issue-metrics-time-range` spec explicitly require the CFD to follow the selector; a selector that leaves one chart frozen contradicts the "re-bases the whole page" goal.
- *Add a separate `windowDays` field to the DTO.* Unnecessary; `rangeFrom`/`rangeTo` already expose the computed window. Rejected (also a non-goal: no new fields).

### D3 — Quality double-window stays in shape; the range drives the primary slot, `Window7d` stays a fixed 7d lens

`GetQualityAsync(projectId, now, windowDays)`:
- `Window7d` is computed exactly as today (fixed 7d) regardless of range — it answers a different question ("last week's first-time-right") and the spec mandates it stays fixed.
- The range drives the `Window30d` slot's length (so at `90d` that slot spans 90 days), **its** previous comparison window (same length, immediately preceding), and the **trend span** + bucket count (`windowDays` daily buckets, dense).
- Omit ⇒ `windowDays = 30` ⇒ today's exact shape (Window7d=7d, primary=30d, previous=30d, trend=30 buckets).

No DTO field is added, removed, or renamed — the slot named `Window30d` simply ceases to be literally 30 days when a range is supplied. `SignalSummary`'s `QualityCard` reads `inputs.quality?.window30d` unchanged, so it re-bases automatically. The per-stage rework breakdown and per-day trend remain current-window-only contracts.

**Alternatives considered:**
- *Rename `Window30d` → `WindowPrimary` and add a `windowDays` field.* Cleanest semantically, but it is a DTO rename + new field — explicitly disallowed by the non-goal ("no existing field removed"; "only add range param"). Defer to a future DTO-versioning change.
- *Drop `Window7d` and make the range the only window.* Destroys the short-term lens the QualityPanel sub-title ("Last 7 days") promises. Rejected.

### D4 — Completion endpoint: the range scales the window length; bucket granularity stays, bucket count derives from the window

`GetCompletionBucketsAsync` keeps its `CompletionBucket` (day/week) axis and gains `int? windowDays`:
- **day bucket:** `windowDays` daily buckets (omit ⇒ 30, today's value). Previous window = same day-length immediately preceding.
- **week bucket:** `ceil(windowDays / 7)` ISO-week buckets (omit ⇒ 12 weeks, today's value). Previous window = same week-length immediately preceding.

The day/week granularity axis is orthogonal to the range (range = *how long*; bucket = *how coarse*), matching the existing `bucket` query param. Both `useCompletionThroughput` (day) and `useCompletionTrend` (week) thread the range. Because omit reproduces 30-day / 12-week exactly, the dense-bucket count assertion in the existing completion spec is preserved on the back-compat path.

**Alternatives considered:**
- *Force week bucket to a fixed week count regardless of range.* Contradicts "range drives the current window length for every endpoint". Rejected.
- *Drop the week bucket when range < 14d.* Adds a hidden coupling and a special-case 400/empty; the week+7d combo (~1–2 buckets) is a legitimate, if sparse, view. Rejected — keep it derivable.

### D5 — Agent usage bucket granularity adapts to the range: 7d/30d daily, 90d weekly

`GetUsageTimeseriesAsync(projectId, windowDays)` keeps daily buckets for 7d and 30d (7 and 30 buckets — 30d daily stays legible and matches today's 7d shape scaled up) and switches to **weekly** buckets for 90d (~13 buckets) so the series does not compress into 90 adjacent bars. The `bucketGranularity` field in `AgentUsageTimeseriesDto` already exists and already reports `"day"`; it will report `"week"` for the 90d path. The cumulative-cost-per-ship sub-series follows the same bucket grid. Omit ⇒ today's 7-day / 7-bucket daily series.

Agent cost windowed (`GetCostWindowedAsync`) simply substitutes its 30-day literals for `windowDays ?? 30` on both current and previous windows; all-time rollup (`GetCostRollupAsync`), `todayCost`, and all-time `costPerShip` are untouched by construction (they are computed before/orthogonal to the window).

**Alternatives considered:**
- *90 daily buckets for the 90d range.* Faithful but visually dense and heavier to render/scan; the CFD already owns the "daily population over 90d" story. Weekly is the legible choice and is the mapping the `agent-cost-time-range` spec requires us to record here.
- *Adaptive bucket count capped at N regardless of range.* Introduces a second hidden knob; a fixed granularity-per-range map is simpler to test and document. Rejected.

**Granularity map (recorded per spec requirement):** `7d → day (7)`, `30d → day (30)`, `90d → week (~13)`; omit → `day (7)`.

### D6 — Frontend: `InsightsPage` owns the range; one `InsightsRange` literal threads down; hooks take an optional range and fold it into `queryKey`

- `type InsightsRange = '7d' | '30d' | '90d'`. `InsightsPage` holds `useState<InsightsRange>('30d')`, renders one selector control (three options, no from/to picker), and passes `range` to `SignalSummary`'s data via the five hooks it already calls **and** to `InsightsCharts` as a prop.
- `InsightsCharts` forwards `range` to every panel; each panel passes it to its hook. The range becomes the single page-level source — no chart fetches it independently.
- Each of the eight hooks (`useCompletionThroughput`, `useCompletionTrend`, `useCumulativeFlow`, `useDeliveryTime`, `useStageDuration`, `useQualityMetrics`, `useAgentUsage`, `useCostRollup`) gains an **optional** `range?: InsightsRange` arg: when present it is appended to the URL (`?range=30d`, composed with `?bucket=` where both exist) **and** folded into the `queryKey` tuple (mirroring how `useCompletionTrend` already folds `'week'`). When absent the URL omits the param and the key keeps its existing shape — so `FactoryStatusHeadline` (`useCostRollup()`) and `AttentionHero` (`useApprovalWait()`, unchanged) hit the back-compat branch by construction.
- `EpicProgressList` does not receive `range` (exempt); its `useEpics` key is untouched.
- Window badges (M2) already derive from each response's `rangeFrom`/`rangeTo`/`window`, so they re-base for free once the data does; no badge edit beyond confirming the derived path.

**Alternatives considered:**
- *A URL query param (`?range=30d`) as the source of truth instead of page state.* Adds routing/share-link concerns and a fifth consumer (the router) to coordinate; the spec asks for page-level state ownership. Defer shareable-links to a follow-up.
- *A React context for the range.* Indirection with a single legitimate provider (`InsightsPage`) and a shallow tree; prop-threading is explicit and trivially assertable. Rejected.
- *Make `range` a required hook arg.* Breaks the Dashboard consumers and the "optional ⇒ back-compat" contract. Rejected — it must be optional.

### D7 — Test strategy: parameterize each endpoint spec for range, assert omit-equality, add web selector + queryKey-isolation specs

- **Server (spec track, `IssueMetricsApiSpecs` / `AgentCostRollupApiSpecs` / `AgentUsageTimeseriesApiSpecs`):** for each endpoint add (a) `range=30d`/`90d` drives a window of that length; (b) omit ⇒ byte-identical window to today (assert the bounds the existing tests already pin); (c) `range=bad` ⇒ 400. Update the CFD spec assertions from "fixed 90d" to "90d on omit, range-driven otherwise" (D2). For agent usage, assert the 90d ⇒ weekly granularity map (D5).
- **Server (unit track):** querier unit tests pass `windowDays` directly and assert the derived current/previous windows and bucket counts, independent of string parsing.
- **Web:** `InsightsPage.test.tsx` gains selector state (default 30d, switch invokes hooks with the new range, Signal Summary + charts re-render on the new data); a focused hook test per changed hook asserts the `range` is in both the URL and the `queryKey`, and that switching range produces a **different** key (no stale cache); `FactoryStatusHeadline`/`AttentionHero` tests keep calling the shared hooks **without** a range and still pass (back-compat guard).
- `npm test` (server) and `npm run typecheck -w packages/web` + `npm run test:run -w packages/web` must pass with no regression to the existing metric specs.

## Risks / Trade-offs

- **[Omit-range back-compat is load-bearing for the Dashboard]** → Mitigation: D1 makes omit ⇒ per-endpoint default explicit; D7 adds omit-equality assertions to every endpoint spec and keeps the Dashboard consumers' tests range-less. The shared hooks' optional-arg design (D6) makes "forgot the range" structurally fall into the back-compat branch.
- **[CFD at 7d is barely useful / sparse snapshots]** → Mitigation: the read path already handles "no snapshot in window" (empty series, not an error); a sparse 7d CFD is the operator's explicit choice, and omit/30d/90d remain the common cases. No special-casing.
- **[Completion week-bucket + range rounding]** `ceil(days/7)` can shift the week count by one vs a naive expectation (e.g. 90d → 13 weeks, not today's 12). → Mitigation: documented in D4; omit reproduces 12 weeks exactly, and the week+range combo is secondary to the day-bucket throughput view.
- **[`Window30d` slot no longer literally 30 days]** could mislead a future reader of the DTO. → Mitigation: recorded in D3 + XML doc-comments updated to state the slot length is range-driven (30d on omit); no wire consumer keys off the field *name*.
- **[Agent usage 90d ⇒ weekly granularity changes the bucket shape existing charts assume]** → Mitigation: the chart renders from `buckets[].bucketStart/bucketEnd` returned by the server, so it adapts to whatever granularity is returned; `bucketGranularity` is asserted in the spec for each range.
- **[Two call sites per signal (InsightsPage hooks + panel hooks) must both receive the range]** → Mitigation: D6 threads the range to both `SignalSummary` (via the five page-level hook calls) and `InsightsCharts` (prop); a missing thread surfaces as a stale-badge mismatch caught by the selector spec.

## Migration Plan

**Deploy order:** server first, then web. The server change is strictly additive (new optional `range` param + `int? windowDays`; omit ⇒ today), so it can ship and be validated against the unchanged web/Dashboard before the selector appears.

1. **Server — shared type (D1):** add `MetricsRange` parse/validate helper + the day-count map.
2. **Server — queriers:** add `int? windowDays` to the seven methods (completion, delivery-time, stage-duration, quality, cumulative-flow, approval-wait, cost-windowed, usage) substituting each fixed literal; default each to today's value on null.
3. **Server — routes:** bind `string? range` on the eight endpoints, validate presets ⇒ 400 on unknown, pass `windowDays` through. Supersede the CFD D6 doc-comments.
4. **Server — validate:** `npm test` (server) green, including updated CFD/quality/usage specs and new range scenarios (D7).
5. **Web — hooks:** add optional `range?: InsightsRange` to the eight hooks (URL + queryKey); leave `useApprovalWait`/`useEpics` unchanged.
6. **Web — page:** add the selector + `useState('30d')` to `InsightsPage`, thread `range` to the five signal hooks and to `InsightsCharts` → panels.
7. **Web — validate:** `npm run typecheck -w packages/web` + `npm run test:run -w packages/web` green, including selector, queryKey-isolation, and Dashboard back-compat tests.

**Rollback:** revert in reverse order. The server change introduces no persisted state and no DTO field change, so rolling back the server simply restores the fixed windows; the web revert removes the selector. Because omit-range is the back-compat path, a partially-deployed state (new server, old web) is fully functional — the old web never sends `range`.

## Open Questions

- **Selector control placement/affordance:** header row next to the "Insights" title, or a compact segmented control above the Signal Summary? Purely presentational; either satisfies the spec. Lean: segmented control in the header row for visibility.
- **Week-bucket exact count for `range=30d`:** `ceil(30/7) = 5` weeks (≈35 days of coverage) vs a floor of 4 weeks (≈28). Lean toward `ceil` so the window fully covers the requested day-span; pin in the completion spec test.
- **Should `useApprovalWait` gain an optional `range` arg too (for symmetry) even though no Insights chart uses it?** The server endpoint accepts `range` (contract uniformity), but threading it through the hook is only worth it if an Insights consumer appears. Lean: leave the hook range-less for now (Dashboard-only consumer); the server contract is ready if needed.
