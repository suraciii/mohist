## Why

Users want to see how much token/cost they have spent this week and how it trends over time — the "input" dimension of productivity. Usage data already lives per-session in the Agent/Session context (`AgentSession.Status.UsageSummary`), but there is **no cross-session aggregation today**: `AgentActivity.summary` carries only active/waiting/completed/failed counts and no usage totals, and no time-series endpoint exists. This change adds both a client snapshot and a server time-series aggregation so the Productivity zone (issue G) can consume them, while staying deliberately isolated from completion metrics (issue C), which belong to a different bounded context.

## What Changes

- Add a **client-side usage snapshot**: derive token/cost totals from `useAgentActivity().sessions[].usage` over the activity window (lower-bound approximation), with the UI explicitly marking the "activity window only" scope.
- Add a **server-side time-bucketed usage aggregation endpoint** in the Agent/Session context that returns token/cost totals bucketed by time over a fixed (v1) range.
- No new persistence is required — per-session usage is already persisted via `AgentSession.Status.UsageSummary`, and sessions carry `CreatedAt` for time bucketing; the endpoint aggregates existing session records.

## Capabilities

### New Capabilities

- `agent-usage-aggregation`: Aggregating token/cost usage across Agent/Session records — both the activity-window client snapshot and the server-side time-bucketed aggregation endpoint, within the Agent/Session bounded context.

### Modified Capabilities

<!-- None. This change is deliberately isolated from Issue/completion context (C) and does not alter the existing agent-session-ui transcript or coder-session-tracking requirements. -->

## Impact

- **Client** (`packages/web/src/entities/agent/`): a new client-derived usage snapshot over `AgentActivity.sessions[].usage` (no change to the server `ActivitySummaryDto`); UI scope labeling.
- **Server** (`packages/server/src/Mohist.Server/Api/AgentRoutes.cs`, `AgentSessionQuerier.cs`): a new aggregation query method and route under `/api/projects/{projectRef}/agent/...`.
- **Consumers**: the Dashboard Productivity zone (issue G) is the downstream consumer; not implemented here.
- **Isolation**: no shared endpoints or data mixing with Issue/completion-metric context (C); non-breaking, additive only.
