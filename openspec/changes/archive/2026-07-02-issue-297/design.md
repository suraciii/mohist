## Context

The Dashboard exposes only a *current slice* of work-in-progress (factory-status cards, attention items, pulse). The operator cannot see how each workflow stage's WIP has piled up over time, where a bottleneck is forming, or whether flow is smoothing out — so congestion is invisible until it is acute. The CFD (stacked-area, one band per stage over time) is the flagship "make flow visible" chart of epic #28.

Relevant current state (verified in code):

- **No per-day snapshot table exists today.** Every existing metrics endpoint (cost, usage, completion, quality, stage-duration, delivery-time, approval-wait) computes its series *on the fly* from events/sessions/issues on each request. The stage-population snapshot will be the **first persisted daily cache** in the server — this design establishes that convention rather than copying one. Closest row-table analogs: `InboxItemRow` (`Infrastructure/Data/Inbox/`), `LabelDefinitionRow` (`Infrastructure/Data/Label/`).
- **The stage attribution idiom already exists** in `IssueQuerier.ComputeLatestAttemptStageDurations` (`packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:1513`). It merges all stage events across an issue's runs, orders by `(Time, Id)`, and takes the stage of the **latest `StageStarted`**. "Invalidate-on-restart" is *emergent* — there is no explicit tombstone; a later `StageStarted` (from retry / rerun / rerun-from-stage / a newer run) simply supersedes the earlier one. The snapshot must stay consistent with this surface.
- **The daily-job pattern exists**: `IssueWorkflowReconciliationService` (`Issue/Services/IssueWorkflowReconciliationService.cs:26`) — `BackgroundService` + tunable period + `ReconcileOnceAsync` test seam + `IDbContextFactory<MohistDbContext>` + sweep-swallowing exceptions. The cleaner `EpicReconciliationService` (`Events/Hosting/EpicReconciliationService.cs:34`) adds `IOptions` cadence + keyset pagination.
- **SQLite cannot translate `DateTimeOffset` comparisons on the TEXT `Time` column** (`IssueQuerier.cs:409-414`). Day bounds must be applied in LINQ-to-objects *after* materialization — the established pattern.
- **Dashboard chart baseline exists** (built by prerequisite #294): a first-party SVG chart kit under `packages/web/src/pages/dashboard/charts/` — `ChartContainer` (three-state), `ChartAccessibility` (SR summary + shape legend), `ChartLegend`, `BarSeries`/`LineSeries`/`SegmentedBarSeries`, `useReducedMotion`, `--chart-1..5` theme tokens. **No external chart lib; no stacked-area primitive exists yet.**
- **Palette constraint**: `--chart-1..5` are five grayscale (zero-chroma) tokens, identical in light/dark. CFD is the first chart needing **six** distinguishable stacked series.
- **Cancelled terminal event nuance**: the spec text says "terminal `IssueCancelled`", but the event actually emitted for the cancelled state is `IssueClosed` (`com.mohist.issue.closed`, `Issue.Transitions.cs:211`). `com.mohist.issue.cancelled` is catalog-listed (`EventCatalog.cs:128`) but **never produced** — implementation must read `IssueClosed`.

Constraints / stakeholders:

- Per `design/architecture.md`: the snapshot computation reads already-persisted events; authoritative interpretation stays in the Server; the widget is read-only and performs no domain writes.
- Per `design/testing.md`: the daily job's cadence loop is structurally untestable; tests must drive a sweep via a public seam (no wall-clock `Task.Delay` in tests), and all time logic must be injectable.
- New persistence is isolated from every existing contract (spec.md:114-128); no backfill (spec.md:76-90).

## Goals / Non-Goals

**Goals:**
- Persist one stage-population snapshot row per project per day (one `int` count per stage: `backlog / plan / build / check / integrate / done`), produced by a daily background job.
- Attribute each in-flow issue to exactly one stage as of the snapshot day under the **same** latest-attempt / latest-run-wins / invalidate-on-restart idiom the stage-duration surface uses, so the two surfaces cannot disagree.
- Idempotent daily writes (re-running for the same project+day does not duplicate or change counts); no historical backfill.
- Expose an additive, project-scoped read endpoint returning the snapshot series over a fixed trailing window (200 + empty series when none; 404 for unknown project).
- Mount a stacked-area CFD widget in the Productivity zone, composing against the existing chart baseline, with full loading/error/empty states.

**Non-Goals** (per proposal/spec):
- No click-through drilldown on a band; no configurable stage filtering; no historical backfill.
- No change to the workflow engine, issue lifecycle, runner, or any existing API contract beyond the additive snapshot read.
- No chroma overhaul of the `--chart-*` palette (deferred — see Open Questions).

## Decisions

### D1. Daily job mirrors `IssueWorkflowReconciliationService`; borrows IOptions cadence + keyset pagination from `EpicReconciliationService`.

New `StagePopulationSnapshotService : BackgroundService` with:
- `IDbContextFactory<MohistDbContext>` + `IGrainFactory` (unused for writes, but kept consistent) + `TimeProvider` (for the snapshot day) + `ILogger` + `IOptions<StagePopulationSnapshotOptions>` (period, default 1 day).
- `ExecuteAsync` = `Task.Delay(period)` → `while(!ct) { try SnapshotOnceAsync catch-log; try Task.Delay(period) catch-cancel return }` — identical control flow to the two reconciliation services (per-sweep exceptions are swallowed so one bad day never kills the loop).
- Public `SnapshotOnceAsync(CancellationToken)` **test seam**: tests call it directly and never start the hosted loop (so the 1-day `Task.Delay` is never observed). This is exactly why no `FakeTimeProvider` is needed for the *cadence* path.
- The sweep enumerates **all projects** in `(ProjectId)` keyset batches (mirroring `EpicReconciliationService`'s keyset walk), and for each project computes one snapshot row as of `TimeProvider.GetUtcNow()`'s UTC day.

Rationale: the spec mandates the `IssueWorkflowReconciliationService` pattern. `IOptions` (over the `static TimeSpan` field) is chosen because it is testable without mutating global state and already proven by `EpicReconciliationService`. Placement: under `Events/Hosting/` (not a feature slice) since the job reads across slices and must not form a slice-internal cycle — the documented rationale at `EpicReconciliationService.cs:27-32`.

### D2. Attribution is extracted into a shared pure function the snapshot and stage-duration both call — never duplicated.

The consistency requirement (spec.md:48-74) is load-bearing: the snapshot and `workflow-stage-duration-metrics` must not disagree on which stage is an issue's latest. The only safe way to guarantee that is a single code path. Therefore extract the core of `ComputeLatestAttemptStageDurations` (`IssueQuerier.cs:1513`) into a pure, testable attribution function that:

1. Takes an issue's time-bounded event stream (`IssueWorkStarted`, `IssueWorkCompleted`, `IssueClosed`, per-run `StageStarted`/`StageCompleted` all filtered to `Time <= snapshotDayEndUtc`) + the ordered workflow stage set.
2. Returns the single attributed stage: `backlog` (no `IssueWorkStarted` as of day) / `done` (terminal done as of day) / excluded (terminal `IssueClosed` as of day) / otherwise the stage of the **latest `StageStarted`** as of day.

Key insight (verified during exploration): *"latest `StageStarted` as of the day"* reproduces the entire idiom — latest-attempt, latest-run-wins, invalidate-on-restart — *for free*, provided the stream is `(Time, Id)`-ordered and time-bounded. A rerun-from-stage and a newer run both emit a later `StageStarted`, which the rule picks automatically. Both `IssueQuerier.GetStageDurationsAsync` (current behavior) and the snapshot call the same function; behavior divergence becomes impossible.

Alternatives considered: **reimplement the rule in the snapshot** (rejected — two copies of subtle temporal logic will drift); **derive the snapshot from the stage-duration surface** (rejected — stage-duration is computed on a *completion* window over `done` issues, the wrong population for a WIP snapshot).

### D3. New isolated `StagePopulationSnapshotRow` table; day stored as `string "yyyy-MM-dd"`; unique `(ProjectId, Day)` index for idempotency.

Entity: plain `XxxRow` POCO in `Infrastructure/Data/StagePopulation/`, namespace `Mohist.Server.Infrastructure.Data.StagePopulation` (matches `LabelDefinitionRow` / `InboxItemRow` style). One row per (project, day) with six `int` count columns (`Backlog/Plan/Build/Check/Integrate/Done`), configured fluently in `MohistDbContext.OnModelCreating` + a new `DbSet<StagePopulationSnapshotRow>`.

- `ProjectId` = `string`, `HasMaxLength(256).IsRequired()` (the repo-wide convention).
- `Day` = `string` formatted `"yyyy-MM-dd"` (UTC), matching the `Boundary` format every existing metrics DTO uses (`IssueRoutes.Dtos.cs:223`). **No `DateOnly`/`DateTimeOffset` persisted** — `DateOnly` is only ever in-memory in `IssueQuerier`, and a string day keeps the key readable and round-trips with the DTO without conversion.
- Unique index `UQ_StagePopulationSnapshots_ProjectId_Day` is the idempotency signal (D4).

Migration: a single additive `CreateTable` + `CreateIndex` (model `20260629003151_AddInboxItemsTable.cs`). No existing table is read or written by the job except the already-existing event/issue read tables.

### D4. Idempotent writes via the unique index + upsert; no backfill.

Per project+day, the job does an **upsert**: if a row for `(ProjectId, Day)` exists, update the six counts in place; otherwise insert. This is implemented via EF's existing idempotency idiom — rely on the unique index as the dedup signal (the `InboxStore.cs:36` pattern catches `DbUpdateException` on conflict). Because the attribution is a pure function of already-persisted events, re-running for the same day yields identical counts, so an upsert and a "select-then-insert/replace" are equivalent; upsert is simpler and race-safe. No snapshot is ever written for a day before go-live (spec.md:76-90); history accrues one day at a time as the job runs.

### D5. Day-bounding applied in LINQ-to-objects after materialization; UTC end-of-day; cancelled = `IssueClosed`.

- The event streams (`IssueEvents` for WorkStarted/WorkCompleted/Closed; `WorkflowRunEvents` for StageStarted/StageCompleted) are loaded with a `Select(...)` projection filtered by `Source`/`Type` sets, then `Time <= snapshotDayEndUtc` is applied **in memory** after `.ToListAsync()` — the SQLite `DateTimeOffset`-on-TEXT translation limit (`IssueQuerier.cs:409-414`) forbids the bound in SQL.
- The snapshot day boundary is **UTC end-of-day**.
- **Cancelled exclusion reads `IssueClosed` (`com.mohist.issue.closed`)**, not the unemitted `com.mohist.issue.cancelled` catalog entry. This is the one place the spec text diverges from the durable facts; the implementation follows the facts.

### D6. Additive read endpoint parallel to `/metrics/stage-duration`.

`GET /api/projects/{projectRef}/issues/metrics/cumulative-flow`, wired through `IssueRoutes.cs` (alongside `MapIssueStageDurationMetrics` at `IssueRoutes.cs:23`), under the issues route group that already applies `ProjectResolutionEndpointFilter`. Consequences fall out for free:
- **404 for unknown project** — the filter returns `NotFound` before the handler runs (`ProjectResolutionEndpointFilter.cs:50`).
- **200 + empty series when no snapshots exist** — handler returns `ApiResults.Ok(new CumulativeFlowResponse(Snapshots: []))`; never an error, never a fabricated snapshot.
- **Fixed trailing window, not caller-configurable** (spec.md:146-150). The window length is the single pinned constant; see D8.
- DTO: `sealed record CumulativeFlowResponse(IReadOnlyList<CumulativeFlowDayDto> Snapshots, string RangeFrom, string RangeTo)`; each `CumulativeFlowDayDto(string Day, int Backlog, int Plan, int Build, int Check, int Integrate, int Done)`. `Day`/`RangeFrom`/`RangeTo` are `"yyyy-MM-dd"` / ISO strings to match sibling DTOs. Time source: injected `TimeProvider.GetUtcNow()`.

The handler reads straight from the snapshot table (no event-stream recomputation on the read path — that is exactly the cost the snapshot exists to avoid, per proposal.md:7).

### D7. CFD widget composes against the chart baseline; a new stacked-area primitive is added.

- Query hook `entities/issue/api/cumulative-flow.ts`: `fetchCumulativeFlow` / `cumulativeFlowQueryKey(['issues','metrics','cumulative-flow', projectId])` / `useCumulativeFlow`, `staleTime: 60_000`, `enabled: !!projectId`, no `refetchInterval` (matches every other metric chart; polling is reserved for live ops data). Re-export from `entities/issue/index.ts`.
- Widget `pages/dashboard/productivity/CumulativeFlowChart.tsx` follows the 5-step sibling structure: `useCumulativeFlow()` → derive `status` via a `hasData` guard → `<section>` + `<ChartContainer status emptyAction={<p>…the CFD gains history once the first daily snapshot lands…</p>}>` → `<ChartAccessibility ariaLabel summary legend viewBox>` → series.
- **New `AreaSeries` primitive** in `charts/`, modeled on `LineSeries`'s `<path>` approach (`LineSeries.tsx:55-71`): builds a stacked area path from per-day per-stage values. Animation via `clipPath`/opacity reveal gated by `useReducedMotion()` (transform/opacity only, `transition: …0.5s ease-out`, `none` when reduced) — consistent with every existing primitive. `tabular-nums` on all axis/legend numerics.
- Mounted in `ProductivityZone.tsx` alongside the other flow charts (ThroughputChart/StageDurationChart).

### D8. Six stacked bands, disambiguated by stacking order + legend shape + label — not hue.

The palette has only five grayscale tokens, but the bands are **positionally distinct** (each stage occupies a fixed stratum in the stack), and the legend already disambiguates by shape+label (the a11y wrapper's non-color contract). So band fill reuses `fill-chart-1..5` by stratum (bottom→top), and stage identity is carried by **order + legend shape + SR summary**, not color hue. This matches the reasoning the sibling design used to keep the grayscale palette acceptable. Adding a `--chart-6` token (palette change) is deferred to Open Questions.

## Risks / Trade-offs

- **Attribution drift between snapshot and stage-duration** → mitigated by D2: one shared pure function, not two copies.
- **SQLite `DateTimeOffset` translation limit** → mitigated by D5: day bounds in LINQ-to-objects after materialization. Acceptance: a project with a very long event history pays an in-memory scan per issue per day; bounded by single-project size on a local tool, and the snapshot is a daily cache so the scan happens once per day, not per render.
- **Spec says `IssueCancelled` but the durable event is `IssueClosed`** → mitigated by D5: read `com.mohist.issue.closed`; documented so reviewers don't "fix" it to the unemitted catalog name.
- **Six stages vs five grayscale tokens** → mitigated by D8: positional stacking + shape/label legend. Residual: adjacent bands of near-equal lightness are low-contrast for full-color viewers; accepted trade-off, chroma deferred.
- **Job failure or host downtime misses a day** → mitigated: per-sweep exceptions are swallowed (loop survives); idempotent upsert means a later run can fill a *missing* day only if the job is taught to backfill — but backfill is an explicit non-goal. Accepted: a missed day stays missing (history is accrual-only).
- **New table + migration is the only schema change** → isolated; rollback is `DropTable`. No existing contract altered.
- **DTO change couples server+web** → additive; old web ignores the endpoint, old server omits the table. Safe either direction.

## Migration Plan

1. **Server (schema + job + read, all additive):** add `StagePopulationSnapshotRow` + `DbSet` + migration (isolated table); add `StagePopulationSnapshotService` + register via `AddHostedService` in `MohistServiceRegistration.cs`; extract the shared attribution function and wire both `IssueQuerier` and the snapshot job to it; add `/issues/metrics/cumulative-flow` endpoint. Deploy via `mo update server`.
2. **Server tests (spec track):** snapshot attribution across backlog/in-flight/done/cancelled + multi-run/retry/rerun-from-stage (latest wins, no double-count); daily-job idempotency (re-run same day → same row, no duplicate); no-backfill (no row before go-live); zero/empty read (`200` empty series); unknown-project (`404`). Unit track: the shared attribution function as a pure function over fixed event fixtures (no DB). Time via `FakeTimeProvider`; cadence via `SnapshotOnceAsync` seam (never start the loop).
3. **Web:** `useCumulativeFlow` + `AreaSeries` primitive + `CumulativeFlowChart`; mount in `ProductivityZone`. Update `tests/setup.ts` matchMedia stub if needed for reduced-motion.
4. **Rollback:** fully additive. Drop the web widget to hide the chart; drop the server endpoint to remove the read; `Down` the migration to remove the table. No existing state to restore. Snapshots accrued before rollback are inert rows.

Verification gates: `npm test` (server, C# warnings-as-errors), `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`.

## Open Questions

- **Fixed trailing-window length:** stage-duration uses 30 days, agent-usage uses 7. A CFD reads best over a longer horizon (60–90 days) to show bottleneck formation, but a longer window is also a taller chart and more rows. Lean: **90 days** (the snapshot table makes the read O(window) regardless of history length, so length is cheap). Confirm before implementation.
- **Snapshot day anchor:** does the job snapshot "as of the current UTC day" (`now`'s day) or "as of the UTC day that just completed" (`now - 1`)? Spec says the first snapshot is the go-live day — either satisfies it. Lean: snapshot the day of `TimeProvider.GetUtcNow()`; the row for today accrues through the day and is finalized on the next run (idempotent upsert makes partial-day re-runs safe). Confirm.
- **Palette chroma / `--chart-6`:** do we add a sixth token (or introduce real chroma into `--chart-1..5`) now for stronger band separation, or keep grayscale and rely on positional+legend disambiguation? Lean: defer (same call the sibling design made); revisit if a third multi-series chart lands.
- **Shared attribution extraction scope:** extracting from `IssueQuerier` touches the stage-duration path. Confirm whether to refactor `GetStageDurationsAsync` onto the shared function in this change (preferred — proves consistency) or land the shared function first and migrate stage-duration in a follow-up (smaller blast radius).
