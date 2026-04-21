## ADDED Requirements

### Requirement: Coder tool call events carry full payload
The `coder_tool_call` event emitted by `runAcpSession()` and `createAcpConnection()` SHALL include `rawInput`, `rawOutput`, and `title` fields in its payload when available. This data already exists in the ACP notification's `toolCall` object but is currently discarded.

**Note**: In `createAcpConnection`, when `onSessionUpdate` is set, `coder_tool_call` is not emitted (see pipeline-session-events spec). This enrichment only applies when `onSessionUpdate` is NOT set (i.e., default Build stage behavior).

#### Scenario: Tool call started with input
- **WHEN** a tool_call sessionUpdate is received with status not 'completed' and the update contains toolCall data
- **THEN** the emitted `coder_tool_call` event includes `rawInput` from `toolCall.input` and `title` from `toolCall.title`

#### Scenario: Tool call completed with output
- **WHEN** a tool_call_update sessionUpdate is received with status 'completed'
- **THEN** the emitted `coder_tool_call` event includes `rawOutput` from `toolCall.output`, `title` from `toolCall.title`, plus `rawInput` from `toolCall.input` if available

#### Scenario: Both runAcpSession and createAcpConnection emit enriched events
- **WHEN** coder_tool_call events are emitted from either `runAcpSession` (Build stage) or `createAcpConnection` (when `onSessionUpdate` not set)
- **THEN** both code paths produce events with `rawInput`, `rawOutput`, `title` fields

### Requirement: SSE event issueId uses issue number for coder sessions
The `coder_text_chunk` and `coder_tool_call` events SHALL use `String(options.issueNumber ?? options.issueId)` as `issueId`. DB operations continue using `options.issueId` (UUID).

#### Scenario: coder_text_chunk with issueNumber
- **WHEN** `issueNumber: 5` is passed in options
- **THEN** `coder_text_chunk` event has `issueId: "5"`
- **AND** `workflowLogRepo.insert` is called with UUID `issueId`

### Requirement: Tool call events include stable toolCallId for deduplication
The `coder_tool_call` event SHALL include a stable `toolCallId` that can be used by the frontend for deduplication. The ID generation logic in `acp-session.ts` (using `sessionId-toolName-counter` pattern) SHALL remain unchanged. Both `runAcpSession` and `createAcpConnection` SHALL use the same ID generation algorithm.

#### Scenario: Frontend deduplicates tool calls by toolCallId
- **WHEN** the frontend receives a `coder_tool_call` event with `toolCallId: "sess-abc-read-0"`
- **THEN** the frontend can use this ID as a Map key for deduplication
- **AND** the same ID appears in both the started and completed events for the same tool call

### Requirement: Workflow log stores tool call data with extractable fields
The `workflowLogRepo.insert()` call in `acp-session.ts` SHALL store the full ACP notification payload (including `toolCall.input`, `toolCall.output`, `toolCall.title`). The frontend SHALL extract these fields from `WorkflowLogItem.data` when reconstructing historical rounds.

#### Scenario: Frontend extracts tool call details from workflow_log
- **WHEN** the frontend loads workflow_log entries for an issue
- **THEN** entries with `eventType: "tool_call"` have `data.toolCall.input` (rawInput) and `data.toolCall.title` available
- **AND** entries with `eventType: "tool_call_update"` and `status: "completed"` have `data.toolCall.output` (rawOutput) available

## MODIFIED Requirements

### Requirement: Coder session mapping persisted on spawn
When `spawn_coder` tool executes and creates an ACP session, the system SHALL record the mapping of issue_id, acp_session_id, execution_id, and a truncated task description to the `coder_session` table with status 'running'. The `coder_tool_call` SSE event SHALL additionally carry `rawInput`, `rawOutput`, and `title` fields so that the WebUI can display tool call details without querying the workflow_log API.

#### Scenario: Spawn coder creates ACP session
- **WHEN** runAcpSession successfully initializes ACP and obtains a sessionId (after `connection.newSession` succeeds)
- **THEN** a coder_session row is created with issue_id (UUID), acp_session_id, execution_id, truncated task (max 200 chars), status='running', and created_at
