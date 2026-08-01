## Context

`AgentSession` is Mohist's stable logical conversation identity for both Agent launches and Workflow work. The Web currently renders those sources through separate data sources: Agent sessions use `/agent-sessions/{sessionId}`, while Workflow sessions resolve a Session ID through an Issue-scoped session name and use Issue routes. This duplicates state mapping, command availability, cache invalidation, and routing decisions.

The Server already exposes source-agnostic reads at `/api/projects/{projectRef}/sessions/{sessionId}` and `/transcript`, with project isolation and source-specific fields absent when inapplicable. Follow-up, cancel, stop, Compact, and Reset already resolve a canonical Session ID in the Session domain. The unified read projection does not yet carry every fact required to explain a Session and choose safe controls.

The Web is a fallback observation and control surface, never a second runtime or state authority. The Session domain retains ownership of inputs, turns, bindings, activity, transcript, and command outcomes.

## Goals / Non-Goals

**Goals:**
- Use one stable-ID Web Session detail route and one shared data source for Agent-launch and Workflow Sessions.
- Extend the unified Session read projection with the source, work context, current-turn, input/turn observations, terminal and failure evidence, model, usage, and runtime-binding facts needed by the page.
- Reuse canonical Session commands and live transcript handling so every visible command outcome converges from Server state.
- Preserve source-aware navigation without inventing Agent or Workflow context.

**Non-Goals:**
- Change AgentSession, AgentJob, Workflow, Runner, or runtime-binding ownership.
- Add a chat workspace, a physical runtime-session history, or client-side retry/recovery decisions.
- Redesign the existing transcript renderer, input attachment model, or provider-specific runtime protocols.
- Preserve the old source-specific Web Session URLs; internal links move to the stable-ID route.

## Decisions

### One canonical Web route and data source

The Web will use `/sessions/:sessionId` as the Session detail route. A new `useUnifiedSessionDataSource` will fetch the unified summary and transcript by stable ID, map them into the existing `SessionDataSourceResult`, and continue using the shared `SessionDetailShell` and transcript widget. All entry points, including Agent history and Workflow Session lists, will link with the stable Session ID.

Navigation is derived from the unified source and context references: Workflow Sessions return to their Issue/Workflow context; Agent-launch Sessions return to their Agent or explicitly recorded context. Source-specific associations absent from the projection are omitted.

Alternative considered: retain `useGenericSessionDataSource` and `useIssueSessionDataSource` and add fields to both. Rejected because command gating, uncertainty handling, and Session mapping would remain duplicated and would keep session-name resolution as a Web concern.

### Unified read projection is the page contract

`UnifiedSessionSummaryDto` and its querier mapping will become the complete read contract for the shared page. It will expose the current turn identifier and source context plus the same activity, input/turn observations, model and usage, terminal result, failure category/reason, tool-error summary, recovery availability, and runtime-binding facts that the page uses for display and command gating. The transcript remains a separate source-agnostic endpoint, filtered by the current runtime binding.

The projection remains a read model: it does not persist a second Session representation or reclassify Workflow work as Agent work. The absent-when-empty source fields remain the guard against fabricated context.

Alternative considered: assemble Agent, Workflow, and runtime state separately in the Web. Rejected because it creates stale cross-query joins and lets the client infer source or command safety.

### Reuse canonical Session command endpoints

The shared data source will submit follow-up, cancel, stop, Compact, and Reset against the canonical Session-ID APIs. It will use the returned acceptance or control result only as an immediate observation, then invalidate the unified summary, transcript, source list, and relevant context query. Existing Session-side rules decide whether a turn is queued or executing and whether a Session is idle enough for Compact or Reset.

Follow-up retries retain the generated idempotency key only after an unknown outcome. Stop remains a request: the UI displays `stop-requested` or `unknown` until the authoritative turn observation reaches a terminal state. Reset retains the logical Session ID and transcript while changing its current runtime binding; the page uses that binding to keep stale events and prior-runtime transcript content out of the current view.

Alternative considered: dispatch Workflow commands through Issue routes and Agent commands through Agent routes after branching on source. Rejected because the Session domain already resolves canonical targets and the Web would duplicate source-dependent command routing.

### Read and live state converge rather than optimistic state

The data source will continue to pass the logical Session ID, current runtime binding, and query keys to `useSessionTranscript`. Mutation success triggers invalidation; live terminal events also invalidate or reconcile the same unified queries. Local state is limited to transient submission UI and the idempotency key needed to retry an unknown follow-up. It never marks an input accepted, a turn terminal, or a Session idle without a Server observation.

Alternative considered: optimistically append inputs and force a terminal status after a command response. Rejected because delivery and interrupt outcomes can be unknown and a reset can replace the runtime binding.

## Risks / Trade-offs

- [A unified projection omits a source-specific fact needed by the page] -> Add that fact to the Server read DTO and mapper with source-aware tests; do not fetch a source-specific endpoint from the shared page.
- [Live events from a superseded runtime binding appear after Reset] -> Keep filtering through the canonical Session ID and current runtime binding, then refetch summary and transcript after recovery completion.
- [A command races with a turn transition] -> Render the Server rejection or unknown outcome, invalidate the unified queries, and recompute available controls from the refreshed observation.
- [Moving internal links invalidates an open old URL] -> This active-development change intentionally has no URL compatibility layer; rollout updates every internal producer and route test together.
- [A long active Session produces frequent reads] -> Reuse existing active-only polling and live-event invalidation; idle Session queries stop polling.

## Migration Plan

1. Extend and test the unified Server summary and transcript projections for both `agent-launch` and `workflow` sources, including project isolation and absent source fields.
2. Add the unified Web API client, query keys, DTO mapping, and shared stable-ID data source. Cover activity, input/turn grouping, source navigation, runtime-lineage filtering, and command outcomes with fake-backed tests.
3. Add `/sessions/:sessionId`, switch all Session links to it, and remove the source-specific detail data sources and routes after their callers move.
4. Deploy Server before Web so the new page never reads an incomplete projection. No data migration is required because Session IDs, transcripts, and command records already exist.
5. Roll back by redeploying the previous Web bundle while retaining the additive Server projection and existing source-specific APIs. No persisted state requires reversal.

## Open Questions

None. The existing canonical Session command APIs and unified read route provide the required Server boundary; implementation chooses the shared Web data source and stable-ID route described above.
