## Why

There is no feedback loop on spend: a user cannot tell what the AI factory costs in aggregate, what it costs today, or what each shipped issue costs — so they cannot judge "is this worth running / should I switch to a cheaper model / should I scope down?" The `InvestmentPanel` ships as an explicit empty shell (its own copy reads "Data unavailable — ... When the usage aggregation hook lands"), and the factory status headline (issue #258) reserves a today-cost slot that is intentionally empty pending this endpoint. Session-level token/cost usage already exists (the Pulse cards show it); only the aggregation is missing.

## What Changes

- Add a project-scoped **token/cost rollup** backend endpoint returning, for a project: `totalCost` (cumulative spend across all sessions with usage), `todayCost` (the current calendar day's spend), `doneIssuesCount` (issues whose status is `Done`), and `costPerShip` (`totalCost / doneIssuesCount`). The rollup is computed purely from the existing per-session `UsageSummary` already recorded on agent sessions (the same source as the existing `/api/projects/{projectRef}/agent/usage` 7-day timeseries); no new event, state collection, or session-domain write is introduced.
- Fill the existing **`InvestmentPanel`** shell with real figures: cumulative spend (`totalCost`), cost-per-ship (`costPerShip`), and the done-issue count used as the denominator. The panel is no longer an empty placeholder. The frontend does not recompute these aggregates over the local session set; it sources them from the rollup endpoint.
- Connect the **today-cost** field in the factory status headline to `todayCost` from the rollup endpoint — the slot #258 reserved and shipped empty. The empty/reserved rendering is replaced by a real numeric value (with a defined empty/zero-sample presentation distinct from a literal zero cost, mirroring the quality-metrics empty convention).
- The existing 7-day daily-bucket usage timeseries endpoint is not removed; the rollup is co-located with it (extended onto the same surface or added alongside — exact shape is a design decision).

## Capabilities

### New Capabilities

- `agent-cost-metrics`: The project-level agent cost aggregation — what "total cost" sums (all sessions with usage), what "today cost" means (current calendar day's spend), what "cost-per-ship" means (`totalCost / doneIssuesCount`, where the denominator is issues at `Done`), how `doneIssuesCount` is derived, the empty/zero-sample handling (no sessions with usage, or zero shipped issues yielding an undefined cost-per-ship), and the backend endpoint that exposes the rollup. Data is sourced exclusively from existing per-session usage; no new collection. Parallel in shape to `ai-quality-metrics`.

### Modified Capabilities

- `dashboard-factory-status`: The reserved today-cost field slot — which currently ships empty pending the rollup endpoint — is connected to the `agent-cost-metrics` `todayCost` value. The "ships empty / reserved placeholder" requirement is replaced by "populated from the rollup endpoint", while the empty/zero-sample case remains distinguishable from a literal zero cost so a missing endpoint response is not mistaken for free operation.

## Impact

- **Backend** (`packages/server/`): A new project-scoped rollup on the existing agent-usage surface (alongside or extending `/api/projects/{projectRef}/agent/usage`), and a new aggregation method following the existing usage-timeseries query path (sum `UsageSummary.CostAmount` across the project's sessions for `totalCost`; restrict to the current calendar day for `todayCost`; count `Done` issues for `doneIssuesCount`; divide for `costPerShip`). The session usage data already lives on `AgentSessionStatusSnapshot.UsageSummary`; no schema change. Currency handling (mixed currencies across sessions) is a design decision for design.md.
- **Web** (`packages/web/src/pages/dashboard/productivity/`): Replace the `InvestmentPanel` empty shell with rendered figures from the new rollup endpoint via a new `entities/issue` (or `entities/agent-session`) API hook mirroring `useQualityMetrics` / `useCompletionTrend`. Wire `todayCost` into the factory status headline's reserved today-cost slot (`packages/web/src/widgets/` factory-status widget from #258).
- **Dependencies**: Depends on #258 (first-screen refactor), which reserved the headline today-cost slot. Belongs to epic #23 (Dashboard control room).
- **Tests**: Aggregation logic (total/today/cost-per-ship, done-issue denominator, zero-sample handling, calendar-day boundary for `todayCost`, sessions outside range) and the endpoint contract; `InvestmentPanel` rendering of figures and the empty state; headline today-cost wiring.
- **Non-Goals**: No $/LOC (LOC is not value — a false metric). No budget hard cap / alerting (this change only surfaces spend and cost efficiency). No per-model or per-prompt drill-down (totals only). No change to the existing 7-day daily-bucket timeseries contract beyond co-location.
