## MODIFIED Requirements

### Requirement: WebUI subscribes to agent detail SSE events
The WebUI SSE subscription SHALL include the following event types: `agent_text_chunk`, `main_tool_call`, `coder_text_chunk`, `coder_tool_call`, `ralph_task_update`, `ralph_loop_progress`, `plan_round_start`, `plan_session_update`, `coder_recovery_status`. All 4 registration arrays must be kept in sync: `event-bus.ts` EventMap, `events.ts` ALL_EVENT_TYPES, `agent-events.ts` AGENT_DETAIL_EVENTS, `useSSE.ts` eventTypes.

#### Scenario: Agent starts and streams text
- **WHEN** the Main Agent emits agent_text_chunk events
- **THEN** the WebUI receives and accumulates the text chunks into a buffer, rendering them as streaming text in real-time

#### Scenario: Plan round start event received
- **WHEN** a `plan_round_start` event is received via SSE
- **THEN** the event is dispatched to the global event emitter for SessionTimeline to consume

#### Scenario: Recovery status event received
- **WHEN** a `coder_recovery_status` event is received via SSE
- **THEN** the event is dispatched to the global event emitter for SessionTimeline to consume
- **AND** the SessionTimeline renders a recovery status indicator

## ADDED Requirements

### Requirement: SessionTimeline displays recovery status indicators

When the SessionTimeline receives `coder_recovery_status` events, it SHALL display contextual status indicators to inform the user about LLM stream hang recovery.

#### Scenario: Hang detected — show warning indicator
- **WHEN** a `coder_recovery_status` event arrives with `status: 'detected'`
- **THEN** the SessionTimeline SHALL display a warning indicator: "LLM 连接中断，正在尝试恢复..."
- **AND** the indicator SHALL be visually distinct from normal streaming activity

#### Scenario: Recovery in progress — show progress indicator
- **WHEN** a `coder_recovery_status` event arrives with `status: 'recovering'`
- **THEN** the SessionTimeline SHALL display a progress indicator: "正在恢复 (attempt N)..."

#### Scenario: Recovery succeeded — dismiss indicator
- **WHEN** a `coder_recovery_status` event arrives with `status: 'recovered'`
- **THEN** the SessionTimeline SHALL dismiss the recovery indicator
- **AND** resume normal streaming display

#### Scenario: Recovery failed — show error indicator
- **WHEN** a `coder_recovery_status` event arrives with `status: 'failed'`
- **THEN** the SessionTimeline SHALL display an error indicator with the failure reason
- **AND** the indicator SHALL remain visible until the session ends or a new event stream begins

#### Scenario: Recovery events loaded from historical workflow_log
- **WHEN** the user opens an issue detail page after a session that included recovery attempts
- **THEN** the SessionTimeline SHALL render recovery events from historical `workflow_log` data (`acp_session_hang_detected`, `acp_session_recovery_started`, `acp_session_recovery_succeeded`, `acp_session_recovery_failed`)
- **AND** recovery events SHALL be displayed inline in the session timeline at the correct chronological position
