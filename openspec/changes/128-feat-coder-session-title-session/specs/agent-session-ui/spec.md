## MODIFIED Requirements

### Requirement: WebUI subscribes to agent detail SSE events
The WebUI SSE subscription SHALL include the following event types: `agent_text_chunk`, `main_tool_call`, `coder_text_chunk`, `coder_tool_call`, `ralph_task_update`, `ralph_loop_progress`, `plan_round_start`, `plan_session_update`, `coder_session_started`. All 4 registration arrays must be kept in sync: `event-bus.ts` EventMap, `events.ts` ALL_EVENT_TYPES, `agent-events.ts` AGENT_DETAIL_EVENTS, `useSSE.ts` eventTypes.

#### Scenario: Agent starts and streams text
- **WHEN** the Main Agent emits agent_text_chunk events
- **THEN** the WebUI receives and accumulates the text chunks into a buffer, rendering them as streaming text in real-time

#### Scenario: Plan round start event received
- **WHEN** a `plan_round_start` event is received via SSE
- **THEN** the event is dispatched to the global event emitter for SessionTimeline to consume

#### Scenario: coder_session_started event carries title
- **WHEN** a `coder_session_started` SSE event is received with `title: "T-004: Create Plan"`
- **THEN** the frontend updates the session's display label to `"T-004: Create Plan"`
