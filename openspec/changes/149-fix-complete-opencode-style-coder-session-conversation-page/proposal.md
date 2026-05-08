## Why

The dedicated coder session page still reads like an internal event log: raw Mohist task contracts, noisy context tools, incomplete tool normalization, and inconsistent live versus historical assembly make it hard to understand what Mohist asked and how the coder responded. This change is needed now to complete the opencode-style session experience started in #143 so `/issue/:number/session/:sessionId` becomes a trustworthy, readable conversation transcript rather than a debugging artifact.

## What Changes

- Replace raw-first Mohist prompt rendering with a readable prompt summary that identifies the task, issue, output/artifact target, and optional context, while preserving the full raw prompt behind an audit disclosure.
- Normalize transcript assembly so Mohist prompts, coder text/thinking, tool starts/updates, errors, recovery events, and terminal events produce stable turns and assistant parts in both historical replay and live streaming.
- Improve event ordering and turn boundaries for same-second events, multiple prompts, terminal session closure, legacy sessions with missing prompts, and live refetch parity.
- Strengthen tool identity and merge logic so inferable tool calls do not render as `unknown`, including nested ACP payloads, `toolName`, `name`, `title`, `rawInput`, and `rawOutput.metadata` fallbacks.
- Group consecutive context-gathering tools such as read, glob, grep, list, memread, and memsearch into concise "Context gathered" summaries, with raw tool details still expandable.
- Render file-changing tools such as `apply_patch`, edit, and write as file-level change summaries with created/modified/deleted/moved status, additions/deletions where available, file accordions, and raw patch payloads as secondary audit details.
- Update the session header and transcript metadata to communicate issue, stage, model, turn count, last activity, changed files, transcript warnings, and user-facing states such as loading, live, finalizing, completed, failed, and stale.
- Improve loading, empty, legacy, API error, error-part, auto-scroll, and new-content behavior so reading historical content is not interrupted by live updates.
- Add regression coverage for the known #143 gaps: same-second ordering, nested tool id normalization, live/historical parity, apply_patch rendering, error rendering, auto-scroll behavior, and absence of inferable unknown tool cards.

## Capabilities

### New Capabilities

- `session-transcript` — normalized coder session transcript assembly must provide stable conversation turns, assistant parts, tool normalization, prompt summaries, transcript metadata, and live/historical parity for session replay.

### Modified Capabilities

- `agent-session-ui` — the dedicated session page must render Mohist/Coder conversation flow, prompt summaries, grouped context tools, file-level patch summaries, readable session status, and robust live scroll behavior.
- `http-api` — coder session detail responses must expose structured transcript metadata and enough normalized part data for the frontend to render the transcript without re-inferring ordering or tool identity from raw logs.
- `coder-session-tracking` — coder session event capture must preserve prompt metadata, tool identity fields, tool start/update correlation, changed-file signals, and terminal status information needed for readable transcript replay.
- `pipeline-session-events` — live SSE and persisted session events must remain consistent enough that appending live events and refetching historical detail produces the same transcript shape.
- `session-timeline-ui` — existing session/timeline views that surface coder sessions must align with the conversation transcript model instead of exposing noisy event-log-style tool rows as the primary experience.

## Impact

- **Backend transcript assembly**: `packages/cli/src/services/session-transcript-service.ts` needs stronger ordering, turn closure, tool id/name inference, tool start/update merging, prompt metadata extraction, changed-file summarization, warning metadata, and parity behavior.
- **Session persistence and observers**: `packages/cli/src/services/session-observers.ts`, `packages/cli/src/agent-runtime/agent-session.ts`, `packages/cli/src/db/session-stream-log-repo.ts`, and coder session persistence must retain the raw audit data while also preserving fields required for readable replay.
- **API**: `GET /api/issues/:number/coder-sessions/:sessionId` in `packages/cli/src/api/issues.ts` must return richer `metadata`, normalized `turns`, transcript warnings, counts, last activity, changed files, and clear incomplete/legacy markers.
- **Frontend session page**: `packages/cli/web/src/components/SessionPage.tsx`, `SessionTranscriptView.tsx`, `ToolCallCard.tsx`, `useSessionTranscript.ts`, shared types, and related tests must shift from event-log rendering to readable opencode-style conversation rendering.
- **Live updates**: `coder_text_chunk`, `coder_tool_call`, recovery, and completion event handling must update the same transcript shape used by historical replay and must avoid forced scrolling when the user is reading earlier content.
- **Tests and fixtures**: backend transcript tests, API tests, WebUI component/hook tests, and realistic session fixtures must cover multi-turn plan sessions, large prompts, context tool bursts, `apply_patch` payloads, title-only tool identity, errors, stale/finalizing states, and live/refetch consistency.
- **No breaking execution semantics**: coder execution, workflow stage transitions, raw prompt/raw payload availability, and read-only session behavior remain unchanged; this change affects persistence fidelity, normalization, API shape, and presentation.
