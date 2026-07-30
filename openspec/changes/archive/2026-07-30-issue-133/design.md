## Context

Issue 133 makes the Web the configuration-and-verification plane for Mohist Agents. The server already computes the domain facts the Web needs: `AgentReadinessService` derives a Readiness conclusion (Ready / Needs setup / Unknown) from the Agent's `agentConfig`, and `AgentAvailabilityService` derives Availability (can-start-now / waiting reason, active runs, runner capacity, waiting work). The `GET /agents` list response already hydrates **Readiness** per Agent (`AgentQuerier.ListAsync`), and `GET /agents/{id}/status` serves **Availability** for a single Agent.

The gap is on the Web discovery surface and in the journey from "see it" to "verify it":

- `AgentListPage` (`packages/web/src/pages/agent-list/ui/AgentListPage.tsx`) renders only name, runtime/model, and a misleadingly-named local `getAvailabilityStatus` that is just the active/archived **lifecycle**. It does not show description, Readiness, Availability, or workload, even though Readiness already arrives in the list response.
- `AgentDetailPage` already has `ReadinessCard`, `AvailabilityCard`, an edit entry, and a Readiness-gated New Session button. Two refinements are missing: the editor does not state that definition edits affect only future Jobs, and the summary does not render Runtime / Max concurrent runs explicitly.
- `AgentSessionComposerPage` already implements Readiness gating (block Needs setup, allow Unknown with hint), idempotent launch, archived blocking, context refs, and differentiated error feedback — these need verification/consistency, not new mechanism.
- There is **no list-scoped Availability serving**: Availability is only reachable one Agent at a time, so the list cannot show it without an N-request fan-out.

Constraints: Web follows Feature-Sliced Design (`shared → entities → features → widgets → pages → app`, enforced by `check:fsd`); tests must not touch real network/process/Runner/wall-clock (fakes via MSW and injected data hooks); the project is in active development (no version-compatibility concern).

## Goals / Non-Goals

**Goals:**
- Turn the Agents list into a discovery/judgment surface: identity, purpose, server Readiness, server Availability, and active/queued workload per row.
- Serve list-scoped Availability/workload without N per-Agent round-trips or per-row polling storms.
- Make the detail page a complete configuration surface (definition summary + edit entry with future-Jobs timing, Readiness gap explanation + next step, Availability distinct from Readiness).
- Keep the test-launch path correct end-to-end (entry from detail, no config overrides, idempotent, lands on the work, gated by Readiness) with consistent differentiated feedback.

**Non-Goals:**
- Session follow-up, cancel, and stop (Session surface — separate issue).
- Slack Connection install/config/management.
- Redefining Readiness/Availability/launch domain rules in the Web (Web only presents server facts).
- Rebuilding the dashboard, issue detail, or settings IA; mobile-specific work.
- Changing the per-Agent `GET /agents/{id}/status` detail endpoint (kept as-is for the detail page).

## Decisions

### Decision 1: List-scoped Availability via a separate summary endpoint (not augmenting the list, not web fan-out)

Serve Availability/workload for the whole list from a new `GET /agents/availability` (project-scoped) returning one entry per Agent: `{ agentId, canStartNow, waitingReason, activeRuns, maxConcurrentRuns, capacity{usedSlots,totalSlots}, queuedCount }`. The Web fetches it in parallel with `GET /agents` and joins by `agentId`.

**Rationale.** Availability is hot (active runs, queue depth, runner online/offline change continuously); the definition list (incl. Readiness) is near-static. Bundling them into `GET /agents` would couple refresh cadences and re-fetch + re-render every Agent definition on each 5s poll, and would push grain/runner/jobs work into the definition read model (`AgentQuerier`). A separate summary decouples cadences: the definition list is invalidated by mutations (and/or a long interval), the availability summary polls ~5s — matching the existing `useAgentStatus`/`useAgentActivity`/`useAgentDetailStatus` 5s polls.

**Alternatives.**
- *Augment `GET /agents` response* — single request, but couples cadences and mixes Availability (grain + runner + jobs) into the definition querier. Rejected.
- *Web fan-out N × `GET /agents/{id}/status`* — N HTTP round trips, N per-Agent polling timers, and N redundant runner-status fetches (each call re-reads online runners). Rejected for scaling with Agent count.

### Decision 2: Compute runner capacity once, reuse across all Agents in the summary

The summary endpoint calls `RunnerStatusService.GetOnlineRunnersAsync(projectId)` **once** to get runner capacity, then reuses it for every Agent's `Compute`. Active runs come from per-Agent `IAgentConcurrencyGrain.GetActiveCountAsync()` (cheap in-process Orleans calls); queued count comes from one batched `AgentJobQuerier` query over the project's pending jobs grouped by Agent. This avoids the redundant runner reads that per-Agent calls would incur.

### Decision 3: Summary carries counts, not the full waiting-work list

The list shows active count and queued count; the per-job waiting-work list stays on the detail page's existing `status` endpoint. This keeps the summary payload O(agents), not O(agents × queued jobs).

### Decision 4: Separate lifecycle from Availability in the list row

Replace the local `getAvailabilityStatus` (which only returns Active/Archived) with two distinct signals in each row: a **lifecycle** affordance (active/archived) and the **server Availability** signal (can start now / waiting + reason + counts). Archived rows are visually distinct and do not require a live Availability value (the summary may omit archived Agents). This is what makes "Runner offline reads as Availability, not a configuration error" hold on the list.

### Decision 5: Web placement — new query in `entities/agent`, join in the page

Add `useAgentListAvailability()` + DTOs in `entities/agent/api` (next to the existing `useAgentDetailStatus`). `AgentListPage` runs `useAgents()` and `useAgentListAvailability()` in parallel and joins by `agentId`. Readiness renders from the Agent record; Availability renders from the summary. Launch mutations (`launchAgentSessionMutationOptions`) invalidate the new query key so returning to the list reflects the new active/queued work. No new FSD layer or cross-slice import is introduced.

### Decision 6: Detail & composer are verify/refine, not rewrite

The detail page and composer already implement most of `agent-detail-definition`, `agent-test-launch`, and `agent-launch-feedback`. Targeted refinements:
- **Edit timing**: the editor (or detail header) states that Instructions/Runtime/Model/Variant/Skills edits apply only to Jobs created after the save (currently only the archive dialog carries timing language).
- **Definition summary completeness**: render Runtime and Max concurrent runs explicitly in the detail summary (today Runtime is derived but not shown; Max concurrent runs appears only inside the Availability card).
- **Feedback consistency**: ensure the same obstruction classes (no-runner / capacity / concurrency / needs-setup / external-unavailable) render consistently between composer and any launch-from-detail path.

## Risks / Trade-offs

- **[Stale Availability on the list vs. authoritative launch]** The 5s poll means the list can briefly show stale Availability. **Mitigation**: the list is a discovery hint, never the launch gate; the server re-checks at launch time (Needs setup blocks; no-runner returns a typed error). No correctness depends on the list's freshness.
- **[Per-Agent grain calls in the summary]** Active count is a per-Agent grain call, so the summary is O(agents) grain calls per poll. **Mitigation**: these are cheap in-process Orleans calls; the detail page already makes the same call per Agent. If scale demands, batch into one concurrency grain later — non-breaking.
- **[Two requests for the list page]** Parallel fetches mean the list depends on two queries. **Mitigation**: render definition + Readiness immediately from `useAgents()` and fill Availability when the summary resolves (graceful: an Agent whose summary hasn't loaded shows Availability as loading/unknown, never as a false Needs setup).
- **[Cadence coupling if a future change bundles Availability into the list]** **Mitigation**: this design deliberately keeps them separate; the summary endpoint is the single Availability source for the list.

## Migration Plan

This change is additive and reversible; the project is in active development, so no compatibility window is required.

1. **Server**: add `GET /agents/availability` (project-scoped) backed by a list-scoped method on `AgentAvailabilityService` that computes runner capacity once and returns per-Agent summary entries. Additive — existing endpoints unchanged, no DB migration, no persistence change.
2. **Web**: add the `useAgentListAvailability()` query + DTOs in `entities/agent`; rewrite `AgentListPage` rows to render description, Readiness, Availability, and active/queued workload; add the edit-timing copy and explicit Runtime/Max-concurrent-runs in the detail summary; wire launch-mutation invalidation to the new query key.
3. **Tests**: spec coverage for list discovery (Readiness/Availability/workload rendering, offline-reads-as-Availability, list-scope single-request serving), detail definition/gap/timing, and feedback consistency; unit coverage for the new query/DTO mapping. All fakes per `design/testing.md`.
4. **Rollback**: revert Web rendering/query — the new endpoint is unused if the Web does not call it; server endpoint can be removed independently with no data impact.

## Open Questions

- Exact summary endpoint path and response envelope (proposed: `GET /api/projects/{projectRef}/agents/availability` returning `{ success, data: AgentAvailabilitySummaryEntry[] }`). Confirmed in implementation.
- Whether archived Agents should be omitted from the summary entirely (proposed: yes — the list renders them from the definition list without a live Availability value).
- Poll interval (proposed: 5000 ms, consistent with existing agent status/activity polls).
