# OpenSpec Capability: agent-session-ui

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

### Requirement: Semantic tool parts

The dedicated session page SHALL render normalized tool parts as semantic assistant conversation parts rather than raw event-log rows. Tool summaries SHALL be readable by default, with raw input/output available only through explicit disclosure.

#### Scenario: Context gathering is grouped

- **WHEN** adjacent context tools such as read, grep, glob, list, search, or memory reads appear in an assistant turn
- **THEN** the page renders a compact context group such as `Gathered context · 4 reads, 1 search`
- **AND** the group expands to show individual tool details and raw data

#### Scenario: Bash tools are summarized

- **WHEN** a bash or shell tool part is rendered
- **THEN** the default view shows a human title, command, status, duration where available, and concise output preview
- **AND** full output and raw payload are available through explicit disclosure

#### Scenario: File-changing tools show file summaries

- **WHEN** edit, write, or apply_patch tools change files
- **THEN** the default view shows changed file count, operation, path, and additions/deletions where available
- **AND** raw diff, patch, input, or output details are collapsed by default but expandable

#### Scenario: Unknown tools have useful fallback display

- **WHEN** a tool cannot be fully normalized
- **THEN** the visible title uses the best available display title, title, target, source name, or event label before falling back to `unknown`
- **AND** raw data remains available for debugging

### Requirement: Readable Mohist coder transcript

The dedicated session page SHALL read top-to-bottom as a Mohist prompt followed by a Coder response and resulting output. It SHALL resemble an opencode-style read-only conversation transcript more than a workflow dashboard or event log.

#### Scenario: Conversation speakers are clear

- **WHEN** a user reads the page from top to bottom
- **THEN** Mohist prompt cards are visibly distinct from Coder response parts
- **AND** each assistant response can include text, collapsed reasoning, semantic tools, errors, and file-change output in order

#### Scenario: Reasoning is collapsed by default

- **WHEN** reasoning or thought content exists
- **THEN** it is available behind a collapsed or summarized disclosure
- **AND** it does not dominate the primary transcript reading flow

#### Scenario: File changes appear as transcript output

- **WHEN** a turn or session includes file-changing tool output
- **THEN** touched paths and additions/deletions are visible in a compact transcript output section
- **AND** this output remains part of the conversation rather than a separate dashboard

#### Scenario: The page stays read-only

- **WHEN** the session page is rendered
- **THEN** it does not show a composer, continue-conversation input, stop control, steering control, or stage-control dashboard as part of this issue

### Requirement: Session page reads as a Mohist-to-Coder transcript

The coder session page SHALL present the session as a read-only Mohist-to-Coder transcript instead of an event log, workflow dashboard, or raw tool viewer.

#### Scenario: Prompt-led turns anchor the transcript

- **WHEN** the session page renders a transcript with one or more Mohist prompts
- **THEN** each Mohist prompt appears as the visible turn boundary
- **AND** assistant output is rendered beneath that prompt as ordered assistant parts

#### Scenario: Internal transcript noise stays out of the primary view

- **WHEN** a transcript includes internal tools, placeholders, or raw payload-first records
- **THEN** the primary transcript hides `todowrite`, stale `unknown` placeholders, and duplicate lifecycle fragments by default
- **AND** raw payloads are only shown in secondary expandable details when needed

#### Scenario: File-changing output belongs to the assistant turn

- **WHEN** the assistant applies patches or edits files during a turn
- **THEN** the turn shows compact file-change summaries and expandable diff details as part of that turn
- **AND** changed files do not appear only as detached workflow cards or summaries

