## Context

Insights (epic #30) gives the operator a conclusion-first "how am I doing lately" view. M1 ships the **Signal Summary** skeleton — four verdict sentences (产出节奏 / 交付效率 / 质量信号 / 投入信号) plus an empty chart-placeholder zone — and the **only** new backend capability in the epic: each of the four metrics surfaces returns a *current window* and an *adjacent previous window of equal length* so the frontend can derive a trend arrow and magnitude in a single read.

The four surfaces already exist today, each project-scoped under `GET /api/projects/{projectRef}/...` and resolved by the shared `ProjectResolutionEndpointFilter` (404 on unknown project):

| Surface | Route | Current window W | Clock | Empty-result idiom today |
|---|---|---|---|---|
| Completion | `.../issues/metrics/completion?bucket=` | day→30d, week→12wk | **`DateTimeOffset.UtcNow` (not injected)** | dense zero buckets; **no sample discriminator** |
| Delivery-time | `.../issues/metrics/delivery-time` | 30d (per-issue points) | injected `TimeProvider` | empty `Points[]`; nullable `CycleDays` |
| Quality | `.../issues/metrics/quality` | 7d + 30d windows | injected `TimeProvider` | nullable rate + `int SampleCount` |
| Agent-cost | `.../agent/cost` | cumulative + "today" | injected (via querier) | nullable `Amount` + `SampleCount` |

A fifth surface, **stage-duration** (`.../issues/metrics/stage-duration`), already returns per-stage `AverageSeconds` with a `SampleCount` discriminator — this satisfies the delivery verdict's "name the slowest stage" requirement with **no new backend work**.

Constraints:
- **Strictly additive**: no existing field removed or re-shaped, no schema migration, no API contract break, no new event/ persisted field / data-collection path. All four changes are reads over already-recorded data.
- The repo's hard rule is **no wall-clock in logic** (`design/testing.md`); three of four surfaces already inject `TimeProvider`, but completion does not.
- Web uses **FSD**; there is **no i18n system** (all copy is hardcoded inline); tests are colocated; a11y is segregated from the default `npm test`.
- `prerequisite #320` owns Dashboard cleanup; M1 must **not** touch Dashboard content.

See `proposal.md` for motivation and `specs/` for the formal requirements.

## Goals / Non-Goals

**Goals:**
- Add `/insights` route + sidebar entry rendering a conclusions-first Signal Summary of exactly four fixed verdicts.
- Each verdict shows current value + trend direction (↑/↓/持平) + change magnitude, derived **strictly** from current-vs-previous-window comparison.
- Add a previous-adjacent-window return to all four metrics surfaces (additive), with an empty-result state **distinguishable** from a genuine zero so the frontend can hide a trendless verdict instead of drawing a misleading arrow.
- Each verdict degrades **independently and gracefully** (current-only, no baseline → hide trend; no current samples → "Insufficient data"; never error, never fabricate).
- Ship the M2 chart-placeholder zone.

**Non-Goals** (from issue/proposal/specs):
- No chart migration (M2), no time-range selector (M3), no epic/label drill-down, no fifth dimension.
- No Dashboard changes (#320), no DTO re-shaping of existing fields, no new persisted data.
- No configurable verdict thresholds — M1 uses fixed direction/magnitude rules.

## Decisions

### D1 — Uniform 30-day trend cadence: every verdict compares "trailing 30d" vs "the 30d immediately before"

The specs lock completion and delivery-time to their **existing** fixed window ("previous-adjacent-window … of the same length as the existing fixed trailing window"): completion's `day` bucket and delivery-time are both 30 days. Rather than let each verdict use a different window length (which would make the four trends visually incoherent), quality and agent-cost are also pinned to **30 days** so all four verdicts share one cadence.

- **Completion**: Insights requests `bucket=day` (the existing `useCompletionThroughput` hook already does) and reads the new current/previous 30-day totals.
- **Delivery-time**: existing 30-day window; add current-30d + previous-30d average cycle time.
- **Quality**: use the existing `window30d` as "current"; add a previous-30d first-time-right rate. (The 7d window remains untouched for other consumers.)
- **Agent-cost**: introduce a **new** 30-day windowed spend + per-issue cost (current + previous), distinct from the cumulative rollup.

**Alternatives considered:**
- *7-day weekly cadence* (matches the issue's "本周/上周" illustration). Rejected: completion/delivery specs explicitly tie the previous window to the existing 30-day window; a 7-day window can't be applied to those two without violating "same length as the existing fixed trailing window," and forcing a parallel 7-day window onto completion/delivery would be non-additive. The "本周" wording in the issue is illustrative ("e.g."), not a hard weekly mandate.
- *Per-surface natural window* (mixed 7d/30d/12wk). Rejected for verdict coherence: four different window lengths on one screen reads as noise. 30d is the only length the spec permits for the two locked surfaces, so unifying on 30d is the consistent choice.

**Verdict copy** says "compared to the previous period" (window-agnostic), with the magnitude as the salient detail — so the 30-day window never has to be explained per-verdict.

### D2 — Each surface's empty-result discriminator follows the surface's own established idiom (additive only)

The specs require every previous-window (and completion's current-window total) to be **distinguishable from a genuine zero**. Each surface already has an idiom; the new fields reuse it rather than introducing a new convention:

- **Quality / Agent-cost**: already use `int SampleCount` + nullable value. New previous-window fields reuse this directly (`SampleCount == 0` ⟹ no baseline ⟹ hide trend; genuine zero ⟹ `SampleCount > 0`, value `0`/`0.0` ⟹ render 持平).
- **Delivery-time**: the previous-window average is `double?` (null ⟹ no delivered issues), mirroring the existing nullable `CycleDays` "undefined" marker.
- **Completion — the one gap**: today's surface emits dense zero buckets with **no** sample discriminator, so it cannot today distinguish "no terminal issues" from "terminal issues all cancelled." Because the new totals need that distinction, the totals DTO gains a `SampleCount` (terminal-issue count in the window). This is additive (a new field on the new totals object only); existing `Buckets`/`Window` are unchanged.

**Alternative:** a single repo-wide `hasSamples: boolean` convention. Rejected — it would diverge from the three surfaces that already settled on `SampleCount`, and "additive" is cleaner when each surface extends its own idiom. The frontend verdict layer normalizes all four idioms into one `InsufficientData` discriminated union (see D5), so the surface-level inconsistency is contained behind one pure-derivation boundary.

### D3 — Slowest-stage verdict reuses the existing stage-duration surface (no new endpoint)

The delivery verdict must name the slowest stage. `GET .../issues/metrics/stage-duration` already returns per-stage `AverageSeconds` (null when no sample). The frontend picks `max` over non-null `AverageSeconds`. **No backend change** is needed for this requirement; implementers must not add a new "slowest stage" endpoint. When every stage's average is null (no stage-duration samples), the slowest-stage clause is omitted per the insufficient-data requirement.

### D4 — Opportunistic fix: inject `TimeProvider` into the completion endpoint

Completion is the **only** metrics surface reading `DateTimeOffset.UtcNow` directly (`IssueRoutes.Metrics.cs:29,38`), a testability gap and a deviation from the repo's no-wall-clock rule and from its two sibling endpoints. Since this endpoint is being modified to compute the previous window (`[now-60d, now-30d]`), the new branch must be testable without a real clock. Switch the handler to the already-registered DI `TimeProvider` (same shape as `IssueRoutes.DeliveryTimeMetrics.cs:16`).

This is in-scope as a consistency fix bundled with the feature, not a separate change: it is required to test the new previous-window logic deterministically. No behavior change for callers.

### D5 — Frontend: new FSD `pages/insights` page + pure verdict-derivation model layer; reuse existing hooks, no new query keys

- **Routing/nav**: add `<Route path="insights" element={<InsightsPage />} />` as a sibling inside the existing `/:projectName` layout route in `app/App.tsx` (project-scoping is inherited from `ProjectRouteScope`, identical to Dashboard). Add `{ key: 'insights', label: 'Insights', icon, to: '/insights', scope: 'project' }` to `primaryNav` in `widgets/app-shell/ui/AppSidebar.tsx`.
- **Data**: the backend changes are additive to existing DTOs, so the existing hooks (`useCompletionThroughput`, `useDeliveryTime`, `useQualityMetrics`, `useCostRollup`) plus `useStageDurationMetrics` already carry the enriched payloads — the TS interfaces just gain optional previous-window fields. **No new hooks, no new query keys.** The page composes these existing hooks.
- **Verdict logic** lives in `pages/insights/model/` as **pure functions** (colocated `.test.ts`, no React): input = the four surfaces' DTOs; output = a normalized `Verdict` discriminated union `{ kind: 'full' | 'currentOnly' | 'insufficient', value, direction?, magnitude?, slowestStage? }`. Putting delta/direction/degradation/polarity logic in pure functions keeps it out of components and trivially unit-testable (sub-50ms, no render).
- **Components**: `SignalSummary` renders the four verdicts; `ChartPlaceholder` is a static marked zone ("Charts migrate in a later milestone"). Copy is hardcoded inline (no i18n exists).

### D6 — Direction & magnitude rules per dimension (fixed polarity)

| Verdict | Metric (current vs previous) | ↑/↓/持平 | Favorable | Magnitude type |
|---|---|---|---|---|
| Throughput | completed count | `>`→↑, `<`→↓, `==`→持平 | **↑ favorable** | count delta |
| Delivery | avg cycle time | `>`→↑, `<`→↓ | **↓ favorable** (faster) | relative % change |
| Quality | first-time-right rate | `>`→↑, `<`→↓ | **↑ favorable** | percentage-point delta |
| Investment | spend, and per-issue cost | per metric | **↓ favorable** (cheaper) | relative % change (each) |

- **Counts** (throughput): exact integer equality for 持平.
- **Doubles** (cycle time, cost): relative tolerance for 持平 (`|cur−prev| / max(|prev|, ε) < 1e-9`), to avoid float jitter producing a spurious arrow. Percentages derived from these tolerances.
- **Polarity** is encoded per-dimension in the derivation layer (throughput/quality: up-is-good; delivery/investment: down-is-good) so sentiment coloring is correct without per-component special-casing.

## Risks / Trade-offs

- **[Completion totals have no sample discriminator today]** → The new totals object carries `SampleCount`; the frontend treats `SampleCount == 0` as "no baseline" (hide trend) and `SampleCount > 0, Completed == 0` as a genuine zero (render 持平). Spec-scenario coverage locks this.
- **[~2× read window per surface]** Each querier now scans current + previous (≈2× the event window). → These are low-frequency dashboard reads (`staleTime: 60s`), bounded by the event log size, and the windows are fixed (30d). Acceptable for M1; revisit if a large project shows latency.
- **[Float "持平" tolerance is arbitrary]** → Use a relative tolerance with a floor; cover the near-equal case in unit tests so the threshold is pinned and intentional, not magic.
- **[Quality 30d vs 7d choice]** Picking the 30d window as the trend basis sacrifices the "more current" 7d feel for cadence coherence. → Documented as D1; the 7d window stays available for other consumers and M2 chart migration.
- **[Mixed empty-result idioms across surfaces]** → Contained behind the single `model/` derivation layer (D5), which normalizes all four idioms into one `Verdict` union; components never branch on `SampleCount` directly.
- **[Completion clock change (D4) is slightly beyond "additive read"]** → It is a one-line DI swap with no behavior change, required to test the new branch; bundle it with the feature rather than splitting a PR.

## Migration Plan

**Deploy order:** server first, then web. No database migration, no event-schema change, no breaking contract change.

1. **Server** — add previous-window fields to the four response DTOs + querier logic; switch completion to injected `TimeProvider`. Existing fields/shapes preserved. Old web clients ignore the new fields. Ship server.
2. **Web** — extend the four TS interfaces with optional previous-window fields; add the `pages/insights` page, nav entry, route, `model/` derivation, and chart placeholder. Ship web.
3. **Validate** — `npm test` (server spec+unit) and web `npm run typecheck -w packages/web` + `npm run test:run -w packages/web` must pass; add a colocated `InsightsPage.test.tsx` and pure-logic `model/*.test.ts`.

**Rollback:** revert web (Insights route/nav disappear; no other page affected) and/or revert server (new fields disappear; older web unaffected). No data to undo — the change introduces no persisted state.

## Open Questions

- **Window label in copy:** verdicts say "previous period" generically. If product later wants the literal window ("past 30 days vs prior 30 days"), it's a copy-only change behind the derivation layer — defer to M2 when charts land and windowing becomes user-visible.
- **Investment verdict when only one of spend/per-issue-cost has a baseline:** spec allows independent emptiness per metric per window. M1 will render whichever sub-trend has a baseline and degrade the other — to be confirmed against the Signal Summary visual design during implementation.
