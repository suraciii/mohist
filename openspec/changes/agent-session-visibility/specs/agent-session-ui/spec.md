## ADDED Requirements

### Requirement: WebUI subscribes to agent detail SSE events
The WebUI useSSE hook SHALL subscribe to the following additional SSE event types: `agent_text_chunk`, `main_tool_call`, `coder_text_chunk`, `coder_tool_call`, `ralph_task_update`, `ralph_loop_progress`. These events SHALL be dispatched through a lightweight global event emitter (e.g. an `EventTarget`) so that interested components can subscribe without creating duplicate SSE connections.

#### Scenario: Agent starts and streams text
- **WHEN** the Main Agent emits agent_text_chunk events
- **THEN** the WebUI receives and accumulates the text chunks into a buffer, rendering them as streaming text in real-time

#### Scenario: Agent makes a tool call
- **WHEN** a main_tool_call event is received with state='started'
- **THEN** the WebUI displays the tool call with a loading indicator
- **WHEN** a main_tool_call event is received with state='completed'
- **THEN** the WebUI updates the tool call entry with duration and collapses the result

### Requirement: AgentSessionPanel replaces static blue box
The IssueDetailPage SHALL replace the "Agent is running..." static blue box with an AgentSessionPanel component when an agent is running or has run on the current issue. The panel SHALL display a timeline of agent activity.

#### Scenario: Agent is running on this issue
- **WHEN** the user views an issue detail page and an agent is actively running
- **THEN** the AgentSessionPanel displays: streaming agent text (with typing cursor), tool call timeline (with started/completed/failed states), and coder session progress

#### Scenario: Agent has completed, viewing history
- **WHEN** the user views an issue detail page and the agent has previously run (issue is not in draft)
- **THEN** the AgentSessionPanel loads historical data from GET /agent-session and GET /coder-sessions and displays the full session timeline

#### Scenario: Agent is mid-run when page opens
- **WHEN** the user navigates to issue detail page while the current agent run is still in progress
- **THEN** historical data may be empty for the current run (because Main Agent messages are only persisted after runAgentLoop completes)
- **AND** the AgentSessionPanel relies on live SSE events to render the ongoing session from that point forward

### Requirement: Streaming text rendered with performance optimization
The AgentSessionPanel SHALL render streaming text using a ref-based buffer with batched updates (via requestAnimationFrame or interval), NOT per-chunk setState. The streaming text SHALL display with a typing cursor animation.

#### Scenario: Rapid text chunks received
- **WHEN** 50+ agent_text_chunk events arrive within 1 second
- **THEN** the UI renders smoothly without jank, updating at most 10 times per second

### Requirement: Tool calls displayed as collapsible timeline entries
Each tool call SHALL be displayed as a timeline entry showing tool name, status icon, and duration. Completed tool calls SHALL be collapsible, with args and result viewable on expansion.

#### Scenario: Tool call with args and result
- **WHEN** a main_tool_call event is received with state='completed', args, and result
- **THEN** the timeline entry shows the tool name, a green checkmark, and the duration; clicking expands to show args and truncated result

### Requirement: Coder sessions show nested progress
When a coder session is active, the AgentSessionPanel SHALL show nested entries for the coder's tool calls and text output, sourced from coder_text_chunk and coder_tool_call SSE events.

#### Scenario: Coder session running with tool calls
- **WHEN** coder_tool_call events are received for an active coder session
- **THEN** the panel shows the coder session as an indented sub-timeline with its own tool call entries

### Requirement: Historical and live events merged without duplicates
When the AgentSessionPanel loads, it SHALL first fetch historical data, then append live SSE events. Events SHALL be deduplicated by a composite key of `stepIndex` + `executionId` (for tool calls) or by checking if the same `stepIndex` and `messageIndex` already exist in historical data.

#### Scenario: User opens page after agent run completes
- **WHEN** user navigates to issue detail page after a completed agent run and new live events begin for a subsequent run
- **THEN** historical messages from the previous run are loaded from API, and live SSE events from the new run are appended without duplicating data
- **AND** `main_tool_call` events match their historical counterpart by `executionId` to avoid duplicate timeline entries
