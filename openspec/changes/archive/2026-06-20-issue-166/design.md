## Context

Issue #166 adds usage (token/cost) aggregation in the **Agent/Session bounded context**, as a prerequisite for the Dashboard Productivity zone (issue G). It has two deliberately coupled-but-separate parts:

1. **Client snapshot** — a lower-bound total of token/cost over the sessions in the current activity window.
2. **Server time-series endpoint** — token/cost bucketed by time over a fixed range.

Current state (verified during the proposal/spec phase):

- Per-session usage is **already persisted** via `AgentSession.Status.UsageSummary` (`AgentSession.cs:97`), updated by the `ApplyUsage` transition which emits `AgentSessionUsageRecorded`. No new persistence store is needed — this resolves the ⚠️ the issue body flagged for the Plan phase.
- `useAgentActivity()` (`packages/web/src/entities/agent/api/queries.ts:25`) polls `GET /api/projects/{projectRef}/agent/activity` every 5s and already returns `sessions[].usage` per card (`AgentSessionUsage`).
- `AgentActivity.summary` (`AgentSessionQuerier.cs:203` → `ActivitySummaryDto`) carries only active/waiting/completed/failed/slot counts — **no usage totals**.
- `AgentSessionQuery.ListByLabelsAsync` (`AgentSessionQuery.cs:32`) queries sessions by label and orders by `CreatedAt`, but has no time-range filter; `AgentSessionRow.CreatedAt` is a DB column available for range filtering.
- Existing agent routes live in `AgentRoutes.cs` (`/agent/status`, `/agent/sessions`, `/agent/activity`), all under `/api/projects/{projectRef}/agent`.

Constraints / stakeholders:

- **Context isolation (AC4)**: must stay out of Issue completion-metric context (C). This was the core mistake of old #158 (two contexts stuffed into one issue). No shared endpoints, no mixed DTOs.
- **v1 is fixed (Non-Goals)**: no configurable time range, no multi-currency conversion.
- Downstream consumer is issue G (Productivity zone); this issue does not build the zone UI.

## Goals / Non-Goals

**Goals:**

- Client-side usage snapshot derived purely from the existing activity payload (no extra fetch), with explicit "activity window only" scope labeling.
- Server time-bucketed usage aggregation endpoint built from persisted per-session `UsageSummary`, covering completed and active sessions.
- Endpoint contract documented and verifiable by a reviewer (AC3).
- Clean bounded-context isolation from completion metrics (AC4).

**Non-Goals:**

- Productivity zone UI (issue G), completion metrics (issue C), configurable range/granularity, and multi-currency conversion — all explicitly out of scope for v1.
- Modifying the server `ActivitySummaryDto` (the snapshot is a derived client view, not a server-enriched summary).
- New persistence, indexes, or background projections.

## Decisions

### Decision 1: Snapshot is a client-side selector, not a server summary enrichment

The snapshot is a `useMemo`-derived selector over `useAgentActivity().sessions[].usage`, following the existing `useActivityCards` pattern (`widgets/coder-session/model/activity-cards.ts:113`). A new `useActivityUsageSnapshot()` sums the additive fields (`inputTokens`, `outputTokens`, `totalTokens`, `costAmount`) and echoes `costCurrency`, treating null/missing as 0 and excluding non-additive context-window fields.

**Rationale**: The spec mandates client-side computation with no extra network request; the activity payload already carries per-session usage. Keeping the server `ActivitySummaryDto` unchanged avoids touching the hot `/agent/activity` path and keeps the snapshot (derived view) cleanly separated from the time-series endpoint (authoritative).

**Alternatives considered**:
- *Server-side*: add usage totals to `ActivitySummaryDto`. Rejected — couples snapshot to the polled activity payload, enlarges a hot endpoint, and blurs the "snapshot vs time-series" split. Revisit only if the snapshot needs data not present in the activity window.

### Decision 2: New route `GET /api/projects/{projectRef}/agent/usage` in `AgentRoutes.cs`

A new `group.MapGet("/usage", ...)` handler delegates to a new `AgentSessionQuerier.GetUsageTimeseriesAsync(projectId, ct)`. This keeps all Agent/Session queries under the existing route group and reuses the `ProjectResolutionEndpointFilter` for auth/project resolution, so AC3 (path documented) and the "same not-found/forbidden behavior as other `/agent/*` routes" scenario are satisfied for free.

**Response shape** (new DTOs):
```csharp
record AgentUsageTimeseriesDto(
    DateTime RangeFrom, DateTime RangeTo, string BucketGranularity, // "day"
    IReadOnlyList<UsageBucketDto> Buckets);
record UsageBucketDto(
    DateTime BucketStart, DateTime BucketEnd,
    long InputTokens, long OutputTokens, long TotalTokens,
    double CostAmount, string? CostCurrency);
```

### Decision 3: Reuse `AgentSessionQuery` + `AgentSessionJsonHelper.Usage`; add a time-range filter

Add an optional `from`/`to` (on `CreatedAt`) to `AgentSessionQuery.ListByLabelsAsync` (or a sibling method), query project sessions (label `ProjectId`) within the fixed range ordered ascending, deserialize via `AgentSessionJson.Deserialize`, and read `AgentSessionJsonHelper.Usage(s)` — the **same** helper already used by `GetActivityAsync`/`ListCurrentAsync`, guaranteeing the snapshot and time-series share one usage definition.

**Rationale**: Consistency with existing per-session usage reads; bounded v1 range keeps deserialization cost acceptable; no new store or projection.

**Alternatives considered**:
- *Dedicated `usage` projection column / rollup table*: cheaper reads and O(rows) without JSON deserialize. Rejected for v1 — adds schema + a writer hook (where to update it); premature given the bounded 7-day range. Revisit if the range widens or session volume grows.
- *Bucket in SQL*: push `GROUP BY` into the DB. Rejected — usage lives inside the session JSON `State`, not as queryable columns, so SQL grouping would still require JSON extraction per row. Bucketing in the querier (in-memory over the bounded result set) is simpler and good enough for v1.

### Decision 4: v1 fixed range = last 7 days, daily buckets, UTC

The handler computes `rangeTo = UtcNow`, `rangeFrom = rangeTo - 7d`, and emits 7 daily buckets (UTC day boundaries). It always emits the full set of buckets, filling gaps with zero totals (per the "empty bucket present, not an error" scenario). Range and granularity are server-side constants; unrecognized query params are ignored.

**Rationale**: "本周花了多少" maps naturally to a 7-day window; daily granularity shows "随时间的变化" without per-call config. UTC avoids timezone configuration (a non-goal).

**Alternatives considered**: 14-day/weekly buckets (more history, noisier); calendar-week ("this week", timezone-dependent). Both deferred — kept as tunable constants, no public API change needed to adjust later.

### Decision 5: Context isolation enforced by separate route/querier/DTO

The usage endpoint, its querier method, and its DTOs are Agent/Session-only. They emit only token/cost/bucket/currency fields and never read or return issue stage, status, completion, or readiness data. No code is shared with any Issue-context completion aggregation.

## Risks / Trade-offs

- **[Snapshot is a lower bound, not all-time]** → The activity window is capped (default 50 most-recent sessions), so older sessions are excluded. Mitigated by the mandatory "activity window only" UI label, and by the time-series endpoint covering the full fixed 7-day range independently.
- **[Per-session JSON deserialization cost]** → Reading `UsageSummary` deserializes each session's `State`. For the v1 7-day window the session set is bounded, so this is acceptable. Mitigation: if volume grows, add a dedicated usage projection column (Decision 3 alternative) without changing the endpoint contract.
- **[Mixed currencies in a bucket]** → v1 sums `costAmount` as-is and echoes a single `costCurrency`. If a project truly spans currencies in one bucket, the sum is semantically inconsistent. Mitigation: v1 assumes single-currency deployments (the common case); multi-currency is an explicit Non-Goal. Bucket echoes the currency so a consumer can detect a mismatch.
- **[Bucket timezone]** → Daily buckets are UTC; a user's "today" may differ from UTC today. Mitigation: client formats bucket boundaries for display; timezone config is a Non-Goal for v1.
- **[Endpoint correctness — the stated medium risk driver]** → New public API. Mitigated by reusing the `ProjectResolutionEndpointFilter` (auth parity), reusing `AgentSessionJsonHelper.Usage` (single usage definition), and the always-full bucket set (no silent gaps).

## Migration Plan

Purely additive — no schema, persistence, or data migration.

1. Ship server change first (new route + querier method + DTOs). Existing clients are unaffected; the endpoint is new.
2. Ship client change (snapshot selector + scope label). It reads only already-fetched activity data, so it is safe even if the server endpoint is not yet deployed.
3. Downstream Productivity zone (G) consumes both when ready.

**Rollback**: revert the commit. No data cleanup required — no persisted state was added or changed.

## Open Questions

1. **v1 range/granularity**: propose last-7-days / daily / UTC. Confirm with reviewer before implementation; adjustable as a server constant with no API change.
2. **Currency echo on mixed currencies**: propose echoing the first non-null `costCurrency` seen in a bucket and documenting the single-currency assumption. Confirm acceptable for v1.
3. **Snapshot selector location**: propose `widgets/coder-session/model/usage-snapshot.ts` (beside `activity-cards.ts`). Confirm placement preferred over `entities/agent`.
