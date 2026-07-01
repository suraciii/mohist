## Why

Runner capacity is computed three different ways across surfaces, so the operator cannot tell whether a runner is actually full: the issues sidebar (`/agent/status.capacity`) and the Dashboard pulse (`/agent/activity.summary.slots`) count *active AgentSessions*, while the runner status page and CLI (`/runners[].capacity`) count *runner grain runtime active works*. These diverge whenever a slot is occupied by a work whose AgentSession is not (yet) visible, or vice versa. This must be fixed now because capacity is the signal operators use to decide whether to start another runner, and the divergence silently misleads scheduling decisions.

## What Changes

- Pin a **single source of truth** for runner capacity: used slots = runner grain runtime active workflow works (distinct by owner id), max slots = persisted runner slots — exactly what `RunnerStatusService` already projects. Reuse that projection; do not introduce a second aggregation model.
- `/agent/status.capacity` (`active`/`max`) SHALL be derived from the runner capacity source, NOT from the count of active AgentSessions grouped by runner.
- `/agent/activity.summary.slots` (the Dashboard pulse feed) SHALL be derived from the same runner capacity source, replacing the ad-hoc `active-session-card-count` vs `runner-count + 1` computation.
- `activeAgents` retains its AgentSession visibility semantics (which sessions are currently shown / can enter transcript or activity) but **SHALL NO LONGER** be consumed as a capacity active-slot count.
- **BREAKING (internal contract)**: the numeric source of `/agent/status.capacity.active` and `/agent/activity.summary.slots.active` changes meaning from "active AgentSession count" to "runner active workflow works". Wire shape (`{ active, max }`) is unchanged; only the value's derivation changes.
- Delete or rename logic/tests that assert `activeAgents.Count` equals slot usage.
- No change to runner scheduling, slot allocation, workflow推进, or the AgentSession activity/transcript read model.

## Capabilities

### New Capabilities

- `runner-capacity`: The single, authoritative runner slot-capacity contract. Used slots are the runner grain's runtime active workflow works (workflow-owned, distinct by owner id); max slots are the persisted runner definition slots. All capacity readouts — sidebar (`/agent/status.capacity`), runner status (`/runners[].capacity`), Dashboard pulse, and CLI — SHALL derive from this one source via the existing runner status projection. AgentSession counts (`activeAgents`) SHALL NOT contribute to capacity.

### Modified Capabilities

- `agent-session-visibility`: The "Direct Agent sessions included in active-agents readout" requirement currently couples the active-agents readout with capacity. It is modified to state that the active-agents readout conveys AgentSession *visibility* only, and SHALL NOT be the source of capacity active-slot counts; capacity is sourced from `runner-capacity`.
- `dashboard-pulse`: The slot-usage indicator is modified to source its active/max values from the unified runner capacity projection (`runner-capacity`), instead of the locally-computed active-session-card count versus runner-count-plus-one.

## Impact

- **Server** (`packages/server`):
  - `Api/AgentRoutes.cs` — `AgentStatusResponse.Create` stops grouping `activeAgents` by runner to compute `activeSlotsByRunner`; capacity is sourced from the runner status projection (runtime active works + persisted slots) per online runner.
  - `Workflow/Services/Sessions/AgentSessionQuerier.cs` — `GetActivityAsync` `ActivitySlotUsageDto` is no longer derived from active card count / runner count; it reuses the runner capacity source.
  - Reuse `Runner/Services/RunnerStatusService` (or its underlying grain reads) as the single projection; no new capacity service or duplicate DTO.
- **Web** (`packages/web`): no contract change to `capacity { active, max }` or `slotUsage`; values now reflect runner works. Sidebar (`AppSidebar.tsx`), Dashboard pulse (`PulseZone.tsx` / `activity-cards.ts`), and runtime-decision gating (`derive-runtime-decision.ts` "Runner capacity is full") consume the corrected values unchanged. The one web spot that bypasses the server value — `IssueDetailPage.tsx:410` (`isCapacityFull = activeAgents.length >= maxConcurrent`) — SHALL be changed to gate on the server-provided `capacity.active >= capacity.max`, removing the last client-side use of `activeAgents` as a capacity source (`activeAgents` still drives AgentSession visibility only).
- **CLI** (`packages/cli`): already reads `/runners[].capacity` (`usedSlots`/`totalSlots`); unaffected and now consistent with the sidebar.
- **Tests**: remove/rewrite specs asserting capacity == active AgentSession count; add coverage for the divergence case (runner active works > active AgentSession count ⇒ capacity still reflects runner works). Server spec + web unit expectations updated.
- **Risk** (medium): changes the semantic source of two read-only capacity values; no writes, no scheduling behavior change.
