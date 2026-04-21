## MODIFIED Requirements

### Requirement: WebUI subscribes to agent detail SSE events
The WebUI SSE subscription SHALL include the following event types: `agent_text_chunk`, `main_tool_call`, `coder_text_chunk`, `coder_tool_call`, `ralph_task_update`, `ralph_loop_progress`, `plan_round_start`, `plan_session_update`. All 4 registration arrays must be kept in sync: `event-bus.ts` EventMap, `events.ts` ALL_EVENT_TYPES, `agent-events.ts` AGENT_DETAIL_EVENTS, `useSSE.ts` eventTypes.

#### Scenario: Agent starts and streams text
- **WHEN** the Main Agent emits agent_text_chunk events
- **THEN** the WebUI receives and accumulates the text chunks into a buffer, rendering them as streaming text in real-time

#### Scenario: Plan round start event received
- **WHEN** a `plan_round_start` event is received via SSE
- **THEN** the event is dispatched to the global event emitter for SessionTimeline to consume

### Requirement: Frontend agentStatus uses issueNumber field for matching
The frontend SSE event handlers and agent status detection SHALL use `issueNumber` (number) instead of `issueId` (UUID) for matching. The `AgentRunnerService.getStatus()` endpoint returns both `issueId` (UUID) and `issueNumber` (number). Frontend SHALL compare `agentStatus.issueNumber === issueNumber` for running detection and filter SSE events by `detail.issueId === String(issueNumber)`.

#### Scenario: Agent running detection works correctly
- **WHEN** agent is running on issue #5
- **THEN** `agentStatus.issueNumber === 5` evaluates to `true`
- **AND** the hook resets streaming state and begins accumulating events

#### Scenario: SSE event filtering works correctly
- **WHEN** a `coder_text_chunk` SSE event arrives with `issueId: "5"` (after backend fix)
- **AND** the user is viewing issue number 5
- **THEN** the event passes the filter and is processed

#### Scenario: SSE event for different issue filtered out
- **WHEN** a `coder_text_chunk` SSE event arrives with `issueId: "2"`
- **AND** the user is viewing issue number 5
- **THEN** the event is filtered out

### Requirement: AgentSessionPanel replaced by SessionTimeline
The IssueDetailPage SHALL replace the AgentSessionPanel component with a SessionTimeline component that provides round-based conversation display. The SessionTimeline SHALL load historical data from the workflow_log API and append live SSE events.

#### Scenario: Agent is running on this issue
- **WHEN** the user views an issue detail page and an agent is actively running
- **THEN** the SessionTimeline displays: pipeline status timeline, round-based conversation (collapsible), streaming agent text with typing cursor, and tool call timeline with expandable details

#### Scenario: Agent has completed, viewing history
- **WHEN** the user views an issue detail page and the agent has previously run (issue is not in draft)
- **THEN** the SessionTimeline loads historical data from `GET /api/issues/:number/logs` and reconstructs the full round-based conversation

#### Scenario: Agent is mid-run when page opens
- **WHEN** the user navigates to issue detail page while the current agent run is still in progress
- **THEN** historical data from workflow_log is loaded first, then live SSE events are appended without duplication

### Requirement: Historical and live events merged without duplicates
When the SessionTimeline loads, it SHALL first fetch historical data from workflow_log API, then append live SSE events. Events SHALL be deduplicated using the following strategy:
- **Tool calls**: Use `Map<toolCallId, ToolCallEntry>` to merge started and completed states
- **Text chunks**: No deduplication needed (incremental accumulation)
- **Rounds**: Distinguish by `roundIndex`
- **Cross-run detection**: Detect new run when `plan_round_start` has `roundIndex === 0`

The complex "timestamp proximity and content overlap" strategy is NOT required.

#### Scenario: User opens page after agent run completes
- **WHEN** user navigates to issue detail page after a completed agent run and new live events begin for a subsequent run
- **THEN** historical messages from the previous run are loaded from workflow_log API, and live SSE events from the new run are appended without duplicating data

#### Scenario: Tool call merged from started and completed records
- **WHEN** workflow_log contains both `tool_call` (started) and `tool_call_update` (completed) for the same toolCallId
- **THEN** SessionTimeline displays a single ToolCallEntry with `state: 'completed'` and both `rawInput` and `rawOutput`
- **AND** duplicate tool calls from SSE are filtered by checking `toolCallMap.has(detail.toolCallId)`

### Requirement: Frontend uses RAF throttling for plan_session_update events
The SessionTimeline SHALL implement requestAnimationFrame-based throttling for `plan_session_update` events to prevent UI lockup during rapid streaming (1000+ events in Plan stage). Events SHALL be buffered in a ref and flushed every 100ms using `requestAnimationFrame`.

#### Scenario: Rapid plan_session_update events during Plan stage
- **WHEN** 1000+ `plan_session_update` events arrive within 5 seconds during Plan stage
- **THEN** the UI updates in batches (every 100ms) instead of per-event
- **AND** no frame drops occur during the streaming session
