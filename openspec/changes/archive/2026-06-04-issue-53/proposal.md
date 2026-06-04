## Why

Session transcript loading currently mixes session metadata, projected chat turns, and raw agent session events in one server DTO. This makes transcript pages heavier than necessary, duplicates projection logic across server and client, and leaves workflow-level events without a dedicated raw API for the web UI.

## What Changes

- Add a metadata-only agent session endpoint for issue session details, including identifiers, status, model, stage, timestamps, title, and aggregate counts.
- Add a raw agent session events endpoint that returns ordered `SessionEvent[]` with raw `payload` values exactly as stored.
- Add a raw workflow log endpoint that exposes workflow-level events from the existing `WorkflowEvents` table in creation order.
- Move transcript, timeline, and compact session projection into a single client-side projection module that consumes raw session events.
- Update session transcript loading so initial page fetches retrieve metadata only, with raw events loaded separately when the view needs them.
- **BREAKING** Remove server-side session transcript projection, including assistant turn construction and `WorkflowAgentSessionTranscript.Turns`.
- **BREAKING** Stop using the misleading transcript `workflowLogs` field for raw agent session events.

## Capabilities

### New Capabilities
- None.

### Modified Capabilities
- `http-api`: Defines the clean-break session metadata, session events, and workflow-log endpoint contracts.
- `pipeline-session-events`: Changes session event access from server-projected transcripts to raw ordered event retrieval with client-owned projection.
- `workflow-log`: Exposes workflow-level events as a first-class raw issue log API instead of conflating them with session transcript data.
- `agent-session-ui`: Changes session pages to consume metadata and raw events separately while preserving the rendered transcript experience.
- `session-timeline-ui`: Consolidates timeline and compact reconstruction onto the shared client session-event projection module.

## Impact

- Backend API routes and DTOs for issue workflow sessions, session events, and workflow logs.
- Backend query/storage access for `WorkflowAgentSessions`, `WorkflowAgentSessionEvents`, and `WorkflowEvents`; no schema or index changes are required.
- Removal of server projection code such as `BuildAssistantParts` and transcript `Turns` shaping.
- Frontend data fetching for session pages and transcript views.
- Frontend projection code in the session entity model and timeline reconstruction paths.
- Backend and frontend tests covering endpoint shapes, ordering, projection behavior, and SessionPage endpoint usage.
