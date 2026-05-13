# OpenSpec Capability: coder-session-tracking

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

### Requirement: Tool lifecycle normalization

Coder session transcript assembly SHALL normalize raw tool lifecycle events into one logical tool part per real tool call. `tool_call` and `tool_call_update` events for the same provider call id, ACP call id, nested tool call id, or deterministic correlation key SHALL merge into a single stable transcript part.

#### Scenario: Tool start and update merge by id

- **WHEN** a persisted session contains `tool_call` and `tool_call_update` events for the same `toolCallId`, nested `toolCall.toolCallId`, `id`, or `callId`
- **THEN** the transcript exposes exactly one tool part for that tool call
- **AND** the tool part contains the best available name, title, input, output, status, timestamps, and error data

#### Scenario: No-id tool events merge by correlation

- **WHEN** a tool start event and a later update event do not carry a stable id but share inferable normalized name plus target or title
- **THEN** the transcript merges them into one logical tool part
- **AND** ambiguous name-only fallback correlation adds a transcript warning rather than silently implying certainty

#### Scenario: Inferable tools avoid unknown fallback

- **WHEN** a raw tool event lacks `toolName` but contains a known `name`, title, raw input shape, command, file path, pattern, patch text, todo payload, or raw output metadata
- **THEN** the transcript infers a useful normalized name and display title
- **AND** the visible transcript does not show an orphan `unknown running...` entry

#### Scenario: Tool status is normalized for transcript display

- **WHEN** raw tool lifecycle status is pending, started, running, completed, failed, cancelled, or timeout-like
- **THEN** the transcript exposes an accurate display status of pending, running, completed, failed, or cancelled where available
- **AND** only non-terminal logical tools appear as active/running in the UI

### Requirement: REQ-CST-001 Coder sessions persist liveness fields

Persisted coder session records SHALL store session liveness data needed to understand the current opencode session call without writing session health into issue stage or status.

#### Scenario: New session initializes liveness data
- **WHEN** a coder session record is created for an opencode session call
- **THEN** its status SHALL be `running`
- **AND** `lastDataAt` SHALL be initialized to the session start time
- **AND** `probeSentAt`, `probeDeadlineAt`, and `failureReason` SHALL be empty

#### Scenario: Data refresh is persisted
- **WHEN** runtime observes valid ACP/opencode data for the session
- **THEN** the coder session record SHALL update `lastDataAt`
- **AND** issue `stage` and `status` SHALL NOT be modified by that update

#### Scenario: Probe state is persisted
- **WHEN** runtime transitions a session to `probing`
- **THEN** the coder session record SHALL store status `probing`, `probeSentAt`, and `probeDeadlineAt`

#### Scenario: Failure reason is persisted
- **WHEN** runtime marks a session as failed due to probe timeout, probe send failure, protocol disconnect, or process exit
- **THEN** the coder session record SHALL store status `failed`, terminal timestamp, and `failureReason`

### Requirement: REQ-CST-002 Coder session status remains a session-call state

Coder session status SHALL use only session-call states for this feature: `running`, `probing`, `completed`, `failed`, and `cancelled`.

#### Scenario: No health taxonomy is persisted
- **WHEN** a session is quiet but has not reached the probe threshold
- **THEN** no `quiet`, `stale`, `hung-suspected`, `healthy`, or `recoverable` state SHALL be persisted

### Requirement: Transcript assembly preserves stable tool identity and turn semantics

Coder session tracking SHALL preserve the information needed to reconstruct stable prompt-led turns, merged tool lifecycle state, and readable historical replay across live and completed sessions.

#### Scenario: Tool lifecycle updates resolve to one logical tool

- **WHEN** a tool emits start and update or completion events for the same invocation
- **THEN** transcript assembly merges those events into one logical tool record whenever identity can be inferred
- **AND** replay does not show duplicate running and completed entries for the same tool invocation

#### Scenario: Unknown-tool fallback is last resort

- **WHEN** a tool name is absent or malformed in tracked events
- **THEN** transcript assembly infers tool identity from toolName, name, title, payload shape, or metadata before falling back to `unknown`

#### Scenario: Historical replay stays ordered and readable

- **WHEN** prompts, assistant output, tool updates, and terminal events share close timestamps
- **THEN** transcript assembly still produces deterministic turn ordering with prompts opening turns before assistant activity and terminal events closing them last

### Requirement: Session log persistence preserves deterministic grouped ordering

Session stream logs and workflow logs SHALL support grouped multi-session reads and SHALL preserve deterministic event ordering across mixed legacy second-precision rows and new millisecond-precision rows.

#### Scenario: Batch reads return ordered rows for multiple sessions

- **WHEN** a caller requests logs for multiple session ids
- **THEN** `SessionStreamLogRepo` and `WorkflowLogRepo` can return grouped results for those session ids
- **AND** rows are ordered by `session_id`, `created_at`, and `rowid`

#### Scenario: Legacy second-precision rows remain stable

- **WHEN** multiple historical rows share the same second-precision `created_at`
- **THEN** reads remain deterministic because `rowid` is preserved as the fallback ordering key

### Requirement: New session log writes use millisecond ISO timestamps

New `session_stream_log` and `workflow_log` writes SHALL store `created_at` as millisecond-precision ISO 8601 strings generated by application code instead of SQLite `datetime('now')`.

#### Scenario: New stream log writes capture millisecond precision

- **WHEN** a new session stream log row is inserted
- **THEN** its `created_at` value is generated in JavaScript with millisecond precision

#### Scenario: New workflow log writes capture millisecond precision

- **WHEN** a new workflow log row is inserted
- **THEN** its `created_at` value is generated in JavaScript with millisecond precision

### Requirement: Transcript assembly preserves emitted reasoning and text ordering

Session transcript assembly SHALL preserve the emitted alternation between reasoning and assistant text by closing the currently open opposite stream part before appending the next chunk type.

#### Scenario: Text closes active reasoning before continuing

- **WHEN** a text chunk arrives while a reasoning part is still open
- **THEN** the assembler completes the reasoning part before appending text
- **AND** the stored transcript keeps the original emitted order

#### Scenario: Non-stream parts close active text or reasoning

- **WHEN** a tool, error, or terminal part is appended while text or reasoning is still streaming
- **THEN** the assembler closes the active streaming part before inserting the new part

### Requirement: Tool lifecycle correlation preserves one logical tool call

Session transcript assembly SHALL merge tool lifecycle events into one logical tool part even when start and update events use different synthetic and provider ids.

#### Scenario: Synthetic and provider ids resolve to one tool part

- **WHEN** a tool start uses a synthetic transcript-local id and later updates arrive with the provider tool id
- **THEN** the assembler correlates them to the same logical tool part
- **AND** the transcript does not render orphan `unknown` tool rows for that lifecycle

### Requirement: File-changing tools expose normalized diff metadata

The transcript normalization layer SHALL enrich `apply_patch`, `edit`, and `write` tool parts with canonical file-change metadata for downstream rendering.

#### Scenario: File-changing tools provide shared diff contract

- **WHEN** `apply_patch`, `edit`, or `write` runs in a tracked session
- **THEN** the normalized tool metadata includes changed-file summaries and a unified diff string when one can be produced
- **AND** raw tool payloads remain available for audit/debugging

