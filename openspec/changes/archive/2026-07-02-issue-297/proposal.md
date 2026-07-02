## Why

The Dashboard only exposes a *current slice* of work-in-progress (the factory-status cards, attention items, pulse). The operator cannot see how each stage's WIP has piled up over time, where a bottleneck is forming, or whether flow is smoothing out — so congestion is invisible until it is already acute. This matters now because CFD is the flagship "make flow visible" chart of epic #28, and per-stage WIP-over-time is the one flow signal no existing Dashboard chart derives.

## What Changes

- Introduce a **daily stage-population snapshot**: one persisted snapshot per project per day, recording the WIP count for each workflow stage (backlog / plan / build / check / integrate / done) as of that day. Rebuilding per-day WIP from the durable event stream on every render is too costly, so the snapshot is the persisted cache the chart reads.
- Introduce a **daily background job** (following the existing `IssueWorkflowReconciliationService` `BackgroundService` + tunable-period pattern) that derives each issue's stage as of the snapshot day from already-persisted events (`IssueWorkStarted`, per-run `StageStarted`/`StageCompleted`, terminal `IssueWorkCompleted`/`IssueCancelled`) and writes one snapshot row.
- Define the **multi-run / stage-rerun attribution rule**: an issue is attributed to the single stage it occupied as of the snapshot day under the existing latest-attempt / latest-run-wins, invalidate-on-restart idiom already established by `workflow-stage-duration-metrics` — so a re-attempted or re-run stage does not double-count the issue, and the population stays internally consistent with the stage-duration population.
- **No historical backfill**: snapshots accumulate from the go-live day forward (explicit non-goal); the chart's history grows one day at a time.
- Add a **stacked-area CFD widget** to the Dashboard Productivity zone: x-axis = date, y-axis = issue count, one color band per workflow stage whose width is that stage's WIP that day. The top edge's slope reads as throughput; a band bulge reads as a bottleneck. The widget composes against the reusable `dashboard-charts` baseline (pinned library, theme-token colors, three-state wrapper, accessibility wrapper, `tabular-nums`, `transform`-based motion honoring `prefers-reduced-motion`).
- Expose the snapshot series through a new additive, project-scoped read endpoint over a fixed trailing window.
- Render full **loading / error / empty** states with a concrete next action (CFD gains history once the first daily snapshot lands).
- **Non-Goals**: no click-through drilldown on a band, no configurable stage filtering, no historical backfill.

## Capabilities

### New Capabilities

- `stage-population-snapshot`: The backend daily stage-population snapshot mechanism — the per-project-per-day snapshot storage, the daily background job that produces it from already-persisted lifecycle/stage events, the multi-run / stage-rerun attribution rule (latest-attempt, latest-run-wins, invalidate-on-restart), the no-backfill accumulation contract, idempotent daily writes, and the additive project-scoped read surface returning the snapshot series over a fixed trailing window. This is the data-collection foundation the CFD reads.
- `dashboard-cumulative-flow`: The stacked-area cumulative flow diagram widget mounted in the Dashboard Productivity zone — one band per workflow stage, band width = that stage's WIP per day sourced from the snapshot series, the trailing window, and its loading/error/empty states with a next action — composing against the reusable `dashboard-charts` baseline and performing no domain writes.

### Modified Capabilities

<!-- None. The CFD widget composes against `dashboard-charts` (its requirements are unchanged, same as the other chart widgets); the snapshot storage and daily job are additive new persistence, isolated from every existing contract. -->

## Impact

- **Server** (`packages/server`): a new EF Core entity + migration for the snapshot table (new persistence, isolated from existing tables); a new `BackgroundService` daily job mirroring `IssueWorkflowReconciliationService` (tunable static period for tests, sweep-then-write, idempotent); a querier that derives per-issue stage-as-of-day under the latest-attempt/latest-run attribution; a new additive project-scoped metrics read endpoint (parallel to `/metrics/stage-duration`). All computation reads already-persisted events; the snapshot is the only new write, and it touches no existing domain state.
- **Web** (`packages/web`): a new CFD widget in the Productivity zone and its query hook, composing against the existing chart baseline; no new charting dependency.
- **No changes** to runner, workflow engine, issue lifecycle, or any existing API contract beyond the additive snapshot read.
- **Risk (medium)**: stems from new persistence + a new background job. Mitigated by strict scope isolation (no existing persistence is touched), the no-backfill contract (history accrues, never rewrites), and idempotent daily writes that are safe to retry.
- **Tests**: snapshot attribution across multi-run / rerun / re-attempt (latest stage wins, no double-count); daily-job idempotency and no-backfill; zero/empty snapshot cases; CFD band values sourced from the snapshot series; three-state rendering and the accessibility wrapper (bands distinguishable without color alone).
