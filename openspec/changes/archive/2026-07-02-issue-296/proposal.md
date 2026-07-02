## Why

The Dashboard reduces "一次做对" quality to a single fixed 7-day / 30-day point, so the operator cannot tell whether first-time-right ability is improving or degrading as output scales. This matters now because the first-time-right and per-stage rework classification already exists and is anchored on ship time, so the trend can be exposed as a pure read with no new data collection. Putting quality into the time dimension — and next to the throughput trend — surfaces the "rising throughput but falling FTR" trade-quality-for-speed signal that a single scalar hides.

## What Changes

- Add a **first-time-right (FTR) trend line** to the Dashboard Productivity zone: one point per trailing time bucket, value = the FTR rate (first-time-right shipped ÷ all shipped) within that bucket, sourced from the existing per-check repair classification.
- Overlay an **optional rework-rate series** on the same percentage axis, so FTR and rework can be read together over time.
- Render full **loading / error / empty** states; the empty state names a concrete next action (the line gains data once an issue ships within the window).
- Compose against the already-established **dashboard chart baseline** (pinned library, theme tokens, three-state wrapper, accessibility wrapper, `tabular-nums`, `transform`-based motion). No new chart baseline is introduced.
- The existing 7-day / 30-day single-point `QualityPanel` scalars remain; the trend is an addition, not a replacement.
- No per-stage FTR time series, no FTR drill-down (Non-Goals).

## Capabilities

### New Capabilities

- `dashboard-ftr-trend`: The first-time-right trend line widget mounted in the Dashboard Productivity zone — a per-time-bucket FTR percentage line with an optional rework-rate overlay, plus its loading/error/empty states and empty-state next action. Consumes the existing quality classification; composes against the `dashboard-charts` baseline.

### Modified Capabilities

- `ai-quality-metrics`: The FTR trend line is a per-time-bucket series evaluated across the trailing window. The existing surface exposes only the two fixed 7-day / 30-day single-point windows (one FTR rate, one per-stage rework rate each), not a bucketed FTR series nor a bucketed rework series. The quality surface gains a per-bucket FTR series and per-bucket rework series — both anchored on ship time (reached `Done`), both computed by reusing the existing first-time-right / per-stage-rework classification already specified. The existing 7-day / 30-day single-point aggregate contract and the zero-sample empty result are otherwise unchanged, and no new event, state collection, or workflow-domain write is introduced.

## Impact

- **Web** (`packages/web`): a new FTR-trend widget mounted in the Productivity zone next to `QualityPanel`; new shared chart components are none (reuses the baseline from `dashboard-charts`). The existing `QualityPanel` scalar figures remain.
- **Server** (`packages/server`): extends the project AI-quality surface (`GET /api/projects/{ref}/issues/metrics/quality` and `IssueQuerier.GetQualityAsync`) to expose the per-bucket FTR and rework series. Computed purely from already-recorded per-check repair counts and the ship event (`IssueWorkCompleted`) that the existing single-point aggregation already buckets on — no new events, no domain writes, no new data collection.
- **No changes** to runner, workflow engine, issue lifecycle, or existing API contracts beyond the additive per-bucket series.
- **Tests**: three-state rendering via the shared wrapper and the accessibility wrapper (SR summary, non-color legend distinguishing FTR from rework); trend values sourced from the per-bucket series; empty-state next action; backend per-bucket computation reusing the existing classification, including the zero-sample/empty-bucket case and ship-time anchoring.
