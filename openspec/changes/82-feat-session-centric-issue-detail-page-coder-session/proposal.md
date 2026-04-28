## Why

The Issue Detail page aggregates all coder sessions into a single flat round timeline, making it impossible to distinguish which rounds belong to which session, what model each session used, how long it ran, or which sessions are still active. The `coder_session` table lacks model/stage metadata, SSE events carry no session identifiers for Plan/Review rounds, and the frontend has no concept of "session as a unit" — users see an undifferentiated stream of events with no structure.

## What Changes

- **DB migration v15**: Add `model`, `coder_type`, `stage` columns to `coder_session` table
- **Backend**: Populate new `coder_session` fields on insert (model from config, coderType="opencode", stage from workflow context)
- **SSE enrichment**: Add `coderSessionId` + `model` to `coder_text_chunk` and `coder_tool_call` events; add `acpSessionId` + `coderSessionId` to `plan_round_start` and `plan_session_update` events
- **New SSE events**: `coder_session_started` (with metadata) and `coder_session_completed` (with status/duration)
- **BREAKING**: Frontend `SessionTimeline` component replaced by `SessionList` + `SessionDetail` — sessions become the primary UI unit instead of flat rounds
- **Frontend**: New `useCoderSessions` hook (initial load from API + live SSE updates); `useSessionTimeline` gains optional `coderSessionId` filter to scope rounds to a single session

## Capabilities

### New Capabilities

- `session-list-ui` — Session-centric Issue Detail panel: SessionList, SessionDetail, SessionHeader components with real-time metadata display, useSessionTimeline coderSessionId filter, and SessionTimeline replacement on IssueDetailPage

### Modified Capabilities

- `coder-session-tracking` — DB schema gains model/coder_type/stage columns; SSE events gain session identifiers; Plan/Review events gain acpSessionId/coderSessionId; new coder_session_started/completed lifecycle events

## Impact

- **DB**: Migration v15 on `coder_session` table (3 new nullable columns)
- **Backend**: `acp-session.ts` (session insert + SSE event payloads), `migrations.ts`
- **SSE**: New event types registered in `events.ts`, `agent-events.ts`, `useSSE.ts`, `event-bus.ts`
- **Frontend**: `useCoderSessions` hook, `SessionList`/`SessionDetail`/`SessionHeader` components, modified `IssueDetailPage`, modified `useSessionTimeline`
- **API**: `GET /:number/coder-sessions` response already includes session data but frontend never consumed it; now becomes primary data source
