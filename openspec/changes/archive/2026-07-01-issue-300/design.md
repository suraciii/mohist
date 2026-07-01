## Context

Runner capacity is computed **three different ways** across surfaces today, with two incompatible DTO shapes and no shared source of truth:

| Surface | DTO (shape) | `active`/`used` source | `max`/`total` source |
|---|---|---|---|
| Sidebar — `GET /agent/status.capacity` | `AgentCapacityResponse(Active, Max)` | count of `ActiveAgentDto` grouped by `RunnerId` (`AgentRoutes.cs:144-146`) — **session-cardinality proxy** | sum of persisted grain slots |
| Dashboard pulse — `GET /agent/activity.summary.slots` | `ActivitySlotUsageDto(Active, Max)` | active session-card count (`AgentSessionQuerier.cs:718`) | `runnerCount + 1` heuristic (`AgentSessionQuerier.cs:718`) |
| Runner status page / CLI — `GET /runners[].capacity` | `RunnerCapacityView(UsedSlots, TotalSlots)` | runner grain `ActiveWorks` (workflow owners, distinct by `OwnerId`) — **authoritative** (`RunnerStatusService.cs:81-87`) | runner grain persisted slots |

The first two diverge from the third whenever a slot is occupied by a workflow work whose `AgentSession` is not yet visible (or vice-versa). Operators use capacity to decide whether to spin up another runner, so the divergence silently misleads scheduling.

**The authoritative projection already exists** and is already exposed verbatim at `GET /runners`: `RunnerStatusService.ProjectRunnerAsync` (`RunnerStatusService.cs:55-110`) reads the runner grain's runtime `ActiveWorks` + persisted `GetSlotsAsync()` and returns `RunnerCapacityView(UsedSlots, TotalSlots)`. It is registered scoped (`MigratedServicesRegistrationSpecs.cs:110`) and backed by fake grains in the integration test harness.

**The web is a thin consumer.** Sidebar (`AppSidebar.tsx:231-236`), PulseZone (`PulseZone.tsx:23,38`), ActivityPage StatusBar, and `derive-runtime-decision.ts:377,436` all render/gate on server-provided `{ active, max }` values directly. The "active-session-card-count vs runner-count-plus-one" heuristic named in the proposal lives **on the server** (`AgentSessionQuerier`), not in web. The one web spot that does local capacity math is `IssueDetailPage.tsx:410` (`isCapacityFull = activeAgents.length >= maxConcurrent`), which bypasses `capacity.active`.

Constraints: no new capacity service or duplicate DTO (proposal); no change to runner scheduling / slot allocation / workflow推进 / AgentSession read model; web contract shape `{ active, max }` is preserved (only value derivation changes).

Stakeholders: operators (capacity readout), scheduler (gating), dashboard/sidebar UIs.

## Goals / Non-Goals

**Goals:**
- Pin `RunnerStatusService` as the single source of truth for used/max runner slots.
- `/agent/status.capacity` and `/agent/activity.summary.slots` derive `active`/`max` from that source, identical to `/runners[].capacity` for the same runner set at the same time.
- `activeAgents` retains visibility semantics only; it no longer feeds any capacity count.
- Remove the divergent session-grouping and `runnerCount+1` logic, plus tests asserting them.
- Cover the divergence case (runner active works > visible active AgentSessions ⇒ capacity reflects runner works).

**Non-Goals:**
- No rewrite of the AgentSession activity/transcript read model.
- No new capacity aggregation service or second DTO shape.
- No runner scheduling / slot allocation / workflow推进 changes.
- No web wire-shape change (`{ active, max }` preserved).
- No change to recovery / orchestration / ledger work attribution.

## Decisions

### D1 — Reuse `RunnerStatusService` as the single source; no new service/DTO

Both divergent routes (`/agent/status`, `/agent/activity`) stop computing capacity locally and read it from the existing `RunnerStatusService` projection, exactly as `GET /runners` already does.

- **Alternatives considered:**
  - *New `RunnerCapacityService`*: rejected — duplicates the projection and violates the proposal's "no second aggregation model".
  - *Inline grain reads (`GetRuntimeStateAsync` + `GetSlotsAsync`) in each route*: rejected — duplicates `ProjectRunnerAsync` logic (`RunnerStatusService.cs:81-87`) and its distinct-by-owner workflow filtering; drifts again over time.

### D2 — Aggregate capacity over online runners in one place

Both reconciled routes need an aggregate `(used, max)` summed across **online (available)** runners. To avoid duplicating the sum-and-filter in two route handlers, add a single aggregate accessor on `RunnerStatusService`, e.g. `GetCapacityAsync(projectId) -> RunnerCapacityView(UsedSlots, TotalSlots)`, summing `Capacity` across views whose `Status` denotes an available runner (matching the current `ListAvailableRunnersAsync` "online" semantics). This is a convenience method on the **existing** service — not a second aggregation model.

- `/agent/status` route: call `RunnerStatusService.GetRunnersAsync(project.Id)` once, derive (a) the per-runner `Runners[]` entries (`Active = UsedSlots`, `Max = TotalSlots`) and (b) the summed `Capacity` from the **same** views — guaranteeing the per-runner list and the total are internally consistent. This **removes** `LoadPersistedSlotsByRunnerAsync`, `activeSlotsByRunner`, and the session-grouping. `activeAgents` (still from `WorkflowActivityQuerier.ListActiveAgentsAsync`) is now consumed only for the `ActiveAgents[]` visibility list and the `Running` flag — never for slot counts.
- `/agent/activity` route: call the aggregate `GetCapacityAsync(project.Id)` and pass the resulting `(used, max)` into `GetActivityAsync` so `summary.slots` reflects the unified source. The `runnerIds` parameter (used only for the `+1` heuristic) is dropped.

- **Alternatives considered:**
  - *Sum independently in each route*: rejected — two copies of the same filter+sum, drift risk.
  - *Inject `RunnerStatusService` into `AgentSessionQuerier`*: rejected — couples the AgentSession domain to the runner-resource domain; the querier should stay session-pure and capacity should be supplied by the route (the orchestrator). See D3.

### D3 — Keep DTO wire shapes `{ active, max }`; only change value derivation

`AgentCapacityResponse(Active, Max)` and `ActivitySlotUsageDto(Active, Max)` keep their field names and shapes. The route maps `RunnerCapacityView.UsedSlots→Active`, `TotalSlots→Max`. This is the explicit BREAKING-internal-contract / backwards-compatible-wire outcome from the proposal: same JSON, different (correct) numbers.

- **Alternatives considered:**
  - *Unify everything to `{ usedSlots, totalSlots }`*: rejected — unnecessary web contract break, explicitly out of scope.

### D4 — `GetActivityAsync` receives capacity as a parameter, not via DI

`AgentSessionQuerier.GetActivityAsync` keeps its session focus: the route computes the unified slot values via `RunnerStatusService` and passes them in (replacing the removed `runnerIds` param) so `summary.slots` is built from the supplied values. The querier does no capacity math of its own.

- **Alternatives considered:**
  - *Inject `RunnerStatusService` into the querier*: rejected (D2) — cross-domain coupling and forces runner fakes into querier unit tests.

### D5 — Fix the one client-side capacity math (`IssueDetailPage.tsx:410`)

Change `isCapacityFull` from `activeAgents.length >= maxConcurrent` to use the server's `capacity.active >= capacity.max`, removing the last place the client treats `activeAgents` as a capacity source. `derive-runtime-decision.ts` already does this correctly (`:377`, `:436`) and is the canonical gating path.

### D6 — `AgentStatusResponse.Create` signature change

`Create` drops the `persistedSlotsByRunner` parameter and gains the runner-status views (or the route builds the `Runners[]`/`Capacity` directly). Direct-constructor callers in tests (`RuntimeEntrySpecs.cs:195`) are updated accordingly.

## Risks / Trade-offs

- **[Sidebar `active` may transiently exceed visible agent cards]** — once capacity counts workflow works, a slot can be occupied before its `AgentSession` is visible. -> *Mitigation*: this is the intended, correct behavior (the whole point of the change); the acceptance criterion "runner active works > active AgentSession count ⇒ capacity still reflects runner works" codifies it. Sidebar text stays `{active} / {max}` and remains accurate.
- **[Online-runner filter must match across routes]** — `/agent/status` capacity and `/runners` must agree on which runners count toward `max`. -> *Mitigation*: both derive from `RunnerStatusService`; document that the capacity sum is over available (online) runners, consistent with the existing `AgentStatusResponse.Runners` list.
- **[Hot-path grain reads]** — `/agent/status` is polled every 5s by the web; `GetRunnersAsync` does runtime+slots grain reads per runner. -> *Mitigation*: the route already performed equivalent grain reads (`ListAvailableRunnersAsync` + `GetSlotsAsync` per runner); net read count is comparable. No new polling cadence introduced.
- **[`AgentStatusResponse.Create` signature change breaks test fixtures]** — direct constructor calls exist (`RuntimeEntrySpecs.cs:195`). -> *Mitigation*: update those fixtures as part of the change; scoped and mechanical.
- **[Activity route loses `runnerIds`]** — it was only used for the `+1` heuristic. -> *Mitigation*: drop the param; no other consumer.

## Migration Plan

1. **Server first**: add the aggregate accessor on `RunnerStatusService`; rewire `/agent/status` and `/agent/activity` to source capacity from it; update `AgentStatusResponse.Create` and `GetActivityAsync` signatures.
2. **Web**: fix `IssueDetailPage.tsx:410` gating. No other web change (shapes preserved).
3. **Tests**: rewrite specs asserting `capacity == active AgentSession count` / `runnerCount+1` (`ApiContractSpecs.cs:128-129`, `RuntimeEntrySpecs.cs:65-66,200-201,195`, `AgentSessionSpecs.cs:1263-1264`); add the divergence spec; keep `RunnerStatusProjectionSpecs.cs:274-287,316-318` pinning the authoritative source.
4. **Deploy**: server-only rollout; **no DB migration, no schema/wire change**; backwards compatible on the wire. CLI is unaffected and automatically consistent.
5. **Rollback**: revert the server + web commits; no data to restore (read-only values, no writes).

## Open Questions

- Naming/placement of the aggregate accessor — method on `RunnerStatusService` (e.g. `GetCapacityAsync`) vs. a small static fold helper over `GetRunnersAsync`. Lean: instance method to keep the "online-runner" filter rule owned by the projection.
- Whether to expose the aggregate at a dedicated route or keep it server-internal (current consumers only need it inside the two existing routes). Lean: keep internal; no new public endpoint.
