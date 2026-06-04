## Context

Mohist currently exposes session transcript data through a combined server DTO that mixes session metadata, raw agent session events, and a server-projected chat transcript. The same raw `WorkflowAgentSessionEvents` stream is then projected again in the web client for live transcript updates and timeline reconstruction, which creates duplicated logic and makes refresh/live rendering drift likely.

The combined transcript response is also too heavy for initial session page loads because a request for session header data can pull more than one thousand raw events plus projected assistant parts. The `workflowLogs` field in that response is misleading: it contains raw agent session events, not rows from the workflow-level `WorkflowEvents` table.

The existing storage model already contains the necessary raw data. `WorkflowAgentSessions` stores session metadata, `WorkflowAgentSessionEvents` stores raw session stream events ordered by `Sequence`, and `WorkflowEvents` stores workflow-level events ordered by `CreatedAt`. No schema or index change is required.

Stakeholders are the ASP.NET Core/Orleans backend, the React web UI session page, timeline UI consumers, tests/specs for HTTP contracts, and future UI surfaces that need workflow logs as a first-class concept.

## Goals / Non-Goals

**Goals:**

- Split session access into metadata-only and raw-event endpoints.
- Add a dedicated raw workflow-log endpoint backed by `WorkflowEvents`.
- Remove server-side assistant transcript projection from session responses.
- Move chat, timeline, and compact session view projection into one client module at `entities/session/model/view.ts`.
- Ensure historical page refresh and live rendering use the same event projection semantics.
- Keep endpoint shapes ready for future event cursor pagination by preserving `sequence` on every event.
- Cover backend endpoint shapes/orderings and frontend projection/page usage with tests.

**Non-Goals:**

- No database schema, migration, or index changes.
- No change to the `WorkflowAgentSessionGrain` write path or SSE event protocol.
- No compatibility shim for the old combined transcript DTO.
- No `?since=` cursor pagination in this change.
- No legacy session migration.
- No fix for tool payload duplication or duplicate per-page-fetch behavior.

## Decisions

### 1. Replace the combined transcript API with three raw-layer endpoints

Implement:

- `GET /api/issues/{number}/sessions/{name}` for session metadata only.
- `GET /api/issues/{number}/sessions/{name}/events` for raw `WorkflowAgentSessionEvents` ordered by `sequence`.
- `GET /api/issues/{number}/workflow-log` for raw `WorkflowEvents` ordered by `createdAt`.

Rationale: each endpoint maps to one storage concept and one UI responsibility. Initial session page loads no longer require loading or projecting the event stream, and workflow events become available without being confused with agent session events.

Alternative considered: keep the old transcript endpoint and add flags such as `?includeEvents=false`. This preserves compatibility but keeps one endpoint responsible for multiple concepts and makes the clean DTO boundary less obvious. The issue explicitly calls for a clean break, so the old shape should be removed rather than hidden behind options.

### 2. Keep raw payloads raw on the server

Session event DTOs should expose `payload` directly from `WorkflowAgentSessionEvents.PayloadJson`, and workflow log entries should expose payloads directly from `WorkflowEvents.PayloadJson`. The backend should not narrow payloads into event-specific C# DTOs for projection purposes.

Rationale: the server stores an append-only event stream and should return it as data, not as a presentation model. This prevents a third projection implementation and keeps future event types forward-compatible with clients that can ignore unknown types.

Alternative considered: introduce typed backend DTOs for every session event kind. That could document payload shapes, but it would reintroduce server ownership of view semantics and create churn whenever the agent event protocol evolves.

### 3. Remove server-owned assistant turns

Remove `BuildAssistantParts` behavior and stop returning `WorkflowAgentSessionTranscript.Turns`. Metadata responses must not load raw events to build turns, and event responses must not include assistant/text/tool projections.

Rationale: assistant turns are a view, not persisted state. Keeping them server-side duplicates the client live projection and is the main cause of drift between refresh and live transcript rendering.

Alternative considered: keep server turns for historical rendering and use client projection only for live updates. That preserves the current drift problem and still makes the initial detail endpoint heavy.

### 4. Centralize client projection in `viewSessionEvents`

Create `entities/session/model/view.ts` with `viewSessionEvents(events, kind)`, where `kind` is `chat`, `timeline`, or `compact`. Payload narrowing for raw `SessionEvent.payload: unknown` should live inside this module. Session page transcript rendering should use `viewSessionEvents(events, 'chat')`; timeline reconstruction should either be removed or become a thin wrapper around `viewSessionEvents(events, 'timeline')`.

Rationale: one projection module gives chat, timeline, and compact views shared ordering, event narrowing, assistant chunk handling, reasoning handling, and tool identity semantics. This makes refresh rendering and live rendering comparable for the same ordered event stream.

Alternative considered: maintain separate projection modules per UI surface that share low-level helpers. That may look modular, but it still allows behavior drift at the grouping/ordering layer, which is the core problem this issue addresses.

### 5. Use metadata and raw events as separate frontend queries

`SessionPage` should first fetch `GET /api/issues/{number}/sessions/{name}` for title, status, model, stage, timestamps, and counts. Transcript content should fetch `GET /api/issues/{number}/sessions/{name}/events` when needed and pass the returned events into `viewSessionEvents(events, 'chat')`.

Rationale: this matches the page's loading priorities and keeps the header responsive even for sessions with large event streams.

Alternative considered: fetch metadata and events in parallel on page load. That can be acceptable for transcript surfaces, but the page must not depend on events for initial metadata rendering and should retain the ability to defer event loading.

## Risks / Trade-offs

- `[Risk] Breaking old clients that call the combined transcript endpoint -> Mitigation: Mohist is in development and the issue explicitly excludes compatibility shims; update all in-repo web clients and tests in the same change.`
- `[Risk] Raw `payload: unknown` moves more responsibility to the client -> Mitigation: keep all payload narrowing in `viewSessionEvents` and test representative synthetic streams for chat, timeline, and compact projections.`
- `[Risk] Workflow events and session events may still be confused by naming -> Mitigation: use endpoint and DTO names that distinguish `workflow-log` entries from `session` events, and remove the misleading transcript `workflowLogs` field.`
- `[Risk] Large sessions still require downloading all events when the transcript opens -> Mitigation: preserve `sequence` and `{ events: [...] }` response shape so cursor pagination can be added later without changing event item shape.`
- `[Risk] Projection semantics can regress during consolidation -> Mitigation: compare historical projection from fetched raw events against expected live-equivalent transcript/timeline output in frontend tests.`
- `[Risk] Metadata counts may require scanning the event table if not already stored -> Mitigation: compute aggregate counts with targeted queries using existing `(SessionId, Sequence)` support and avoid loading full payloads for metadata.`

## Migration Plan

1. Add backend query/DTO support for metadata-only sessions, raw session events, and raw workflow log entries.
2. Add HTTP routes for `/api/issues/{number}/sessions/{name}`, `/api/issues/{number}/sessions/{name}/events`, and `/api/issues/{number}/workflow-log`.
3. Remove the old transcript response shape from backend session APIs, including `Turns`, `workflowLogs`, and assistant-part projection code.
4. Add `entities/session/model/view.ts` and move event narrowing/projection for chat, timeline, and compact views into it.
5. Update `SessionPage`, `useSessionTranscript`, and timeline reconstruction to consume metadata/events separately and call `viewSessionEvents`.
6. Add backend tests for endpoint shape, ordering, raw payload preservation, and absence of server-projected turns.
7. Add frontend tests for all projection kinds and SessionPage endpoint usage.

Rollback strategy: because this is a clean-break development change, rollback is reverting the feature changes as a unit. No database rollback is needed because storage schemas and write paths do not change.

## Open Questions

- Which exact aggregate counts should the metadata endpoint expose beyond `eventCount` and `toolCount` where available?
- Should the compact projection return only summary data needed by current UI surfaces, or define a broader stable shape for future compact session lists?
- Should the old `/workflow/sessions/{name}` route be removed entirely or return a not-found/deprecation response during development?
