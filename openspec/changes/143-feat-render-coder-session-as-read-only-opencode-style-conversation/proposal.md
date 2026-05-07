## Why

Coder session detail pages currently do not show the actual conversation that drove the agent: Mohist prompts can be missing, assistant text and tool calls are reconstructed as flat logs, and users cannot audit why the coder agent behaved as it did. This change is needed now because the dedicated session URL should behave like a trustworthy read-only opencode-style session replay, not another issue workflow dashboard.

## What Changes

- Persist every Mohist prompt sent through `AgentSession.execute(prompt)` as a first-class session input, including initial task prompts, plan/check/review prompts, retry prompts, follow-up prompts, continuation prompts, and recovery prompts.
- Reconstruct coder session history as conversation turns where each Mohist prompt opens a turn and subsequent coder text, thinking, tool calls, errors, and recovery events attach to that turn.
- Add explicit turn-boundary behavior for new prompts, session completion/failure/timeout/cancellation, and legacy orphan assistant/tool events.
- Update the session detail API/data shape so the frontend can consume a session transcript or message/part model instead of guessing structure from raw stream logs alone.
- Rework `/issue/:number/session/:sessionId` into a read-only conversation transcript with a session header, Mohist prompt cards, coder agent markdown responses, inline assistant parts, collapsible thinking, and tool-specific expandable details.
- Render tool calls as assistant parts with status, target, timing where available, expandable input/output, bash terminal output, edit/apply_patch diff-like summaries, compact read/grep/glob rows, and a generic fallback for unknown tools.
- Preserve historical replay semantics so completed sessions can be rebuilt without SSE memory, live sessions replay in the same order after refresh, and legacy sessions without recorded prompts show an explicit incomplete-data fallback.
- Keep workflow/task/check/diff information as session context in the header or optional side context; it must not replace the session conversation timeline.
- Keep the page read-only for this change: no composer, no disabled fake input, and no continue-conversation behavior.

## Capabilities

### New Capabilities

- `session-stream-log` — session stream history must preserve Mohist prompt events alongside assistant chunks, thinking, tool calls, tool updates, errors, and recovery events without relying on ACP `user_message_chunk` availability.

### Modified Capabilities

- `session-timeline-ui` — the session timeline must become a conversation-turn transcript rather than a round/workflow/log aggregation.
- `agent-session-ui` — the dedicated session page must render an opencode-style read-only conversation with markdown, assistant parts, tool disclosure, thinking disclosure, streaming feedback, and legacy fallbacks.
- `http-api` — coder session detail responses must expose enough structured transcript/message/part data and trustworthy metadata for the frontend to render historical and live sessions consistently.

## Impact

- **Agent runtime**: `packages/cli/src/agent-runtime/agent-session.ts` must record prompt boundaries before or at `execute(prompt)` calls and preserve state transitions for timeout, cancellation, failure, and completion.
- **Session observers/logging**: `packages/cli/src/agent-runtime/session-observer.ts` and `packages/cli/src/db/session-stream-log-repo.ts` need to persist Mohist prompt events and enough raw assistant/tool/recovery events to rebuild turns.
- **Persistence**: `coder_session` and/or `session_stream_log` data access may need schema/repo changes for prompt kind, prompt timestamp, session context, and reliable completed-at semantics.
- **API**: `GET /api/issues/:number/coder-sessions` or a session-detail endpoint must return structured transcript data, session metadata, and legacy incomplete-data markers instead of leaving the UI to infer all structure from raw logs.
- **Frontend session page**: `packages/cli/web/src/components/SessionPage.tsx`, `SessionTimeline.tsx`, `ToolCallCard.tsx`, `SessionDetail.tsx`, `useSessionTimeline.ts`, `useCoderSessions.ts`, and shared types must shift from round/log rendering to read-only transcript rendering.
- **Rendering dependencies/utilities**: the WebUI may need markdown rendering and safe ANSI handling for readable assistant responses and bash output.
- **SSE/live replay**: existing `coder_session_started`, `coder_session_completed`, `coder_text_chunk`, `coder_tool_call`, `plan_round_start`, and `plan_session_update` flows must remain compatible while allowing refresh-safe transcript reconstruction.
- **No breaking user workflow**: existing issue detail workflow context remains available, but session detail prioritizes the coder session conversation timeline.
