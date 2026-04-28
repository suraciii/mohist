## MODIFIED Requirements

### Requirement: SessionTimeline component renders rounds
The IssueDetailPage SHALL include a SessionTimeline component that renders agent session activity grouped by rounds. Each round SHALL be a collapsible section showing: round label (e.g., "Proposal", "Specs", "Design"), timestamp, and agent output summary. Agent text SHALL be separated into thought text and message text. Thought text SHALL be default-collapsed with a toggle to expand.

#### Scenario: Plan stage with 5 rounds
- **WHEN** the user views an issue that completed the Plan stage (5 rounds: proposal, specs, design, tasks, self-review)
- **THEN** SessionTimeline renders 5 collapsible round sections, each labeled with the round type and timestamp

#### Scenario: Round expanded to show conversation
- **WHEN** the user clicks on a round section
- **THEN** the section expands to show the full conversation: user prompt, agent message text, thought text (default collapsed), and tool call entries with status icons

#### Scenario: Thought text default collapsed
- **WHEN** a round contains thought text and message text
- **THEN** message text is displayed normally, and thought text is rendered inside a collapsible section labeled "Thinking..." that is collapsed by default
- **AND** clicking the "Thinking..." toggle expands it to show the full thought content

#### Scenario: Thought text with character count
- **WHEN** thought text exceeds 500 characters
- **THEN** the collapsed toggle label shows "Thinking... (1.2KB)" with an approximate size indicator

### Requirement: SessionTimeline appends live SSE events
When an agent is actively running on the current issue, SessionTimeline SHALL subscribe to `plan_session_update` and `plan_round_start` SSE events and append them to the current round in real-time. For Build stage, it SHALL also subscribe to `coder_text_chunk` and `coder_tool_call`. The hook SHALL handle `plan_session_update` events with `sessionUpdate` values of `tool_call`, `tool_call_update`, `agent_message_chunk`, and `agent_thought_chunk`.

#### Scenario: Agent starts new round while user is viewing
- **WHEN** the user is viewing the issue page and a new plan round starts
- **THEN** a new round section appears with the round label and begins accumulating agent output in real-time

#### Scenario: Agent text chunks stream into current round
- **WHEN** `plan_session_update` events with `sessionUpdate: 'agent_message_chunk'` arrive
- **THEN** the agent message text in the current round updates with a typing cursor animation, using requestAnimationFrame-batched rendering

#### Scenario: Agent thought chunks stream into current round
- **WHEN** `plan_session_update` events with `sessionUpdate: 'agent_thought_chunk'` arrive
- **THEN** the thought text in the current round accumulates in a separate buffer, rendered in the default-collapsed "Thinking..." section

#### Scenario: Plan stage tool calls via plan_session_update
- **WHEN** `plan_session_update` events with `sessionUpdate: 'tool_call'` arrive with data containing `{ toolCallId, kind, title, rawInput, status }`
- **THEN** a ToolCallEntry is created in the current round with the tool call details

#### Scenario: Plan stage tool call updates via plan_session_update
- **WHEN** `plan_session_update` events with `sessionUpdate: 'tool_call_update'` arrive with data containing `{ toolCallId, status: 'completed', title, rawInput, rawOutput }`
- **THEN** the existing ToolCallEntry is updated with the new `title`, `rawInput`, `state`, and `rawOutput`

#### Scenario: Build stage coder events stream
- **WHEN** `coder_text_chunk` events arrive during Build stage (executionId starts with `build-`)
- **THEN** the text is appended to the current task's round section

### Requirement: Tool calls in timeline show expandable details
Each tool call entry in a round SHALL display a meaningful context label derived from rawInput, status icon, and duration. When the `title` field is absent or equals only the tool kind name, the context label SHALL be derived from `rawInput` using tool-specific extraction rules (file path for read/write/edit, command for bash, pattern for glob/grep). Completed tool calls SHALL be expandable to show input arguments and output result.

#### Scenario: Tool call with read input and directory output
- **WHEN** a tool_call_update with kind 'read' and completed status is rendered, with rawInput containing a file path
- **THEN** the entry shows a green checkmark and the derived file path basename as the context label (e.g., "server.ts")
- **AND** clicking expands to show the full file path and directory listing output

#### Scenario: Tool call with bash command
- **WHEN** a tool_call_update with kind 'bash' and completed status is rendered, with rawInput containing a command
- **THEN** the entry shows a green checkmark and the command string as the context label (e.g., "npm run build")

### Requirement: SessionTimeline loads history from workflow_log API
When the page loads for a non-draft issue, SessionTimeline SHALL fetch historical data from `GET /api/issues/:number/logs` and reconstruct the round-based conversation structure by splitting on `user_message_chunk` events. Historical `tool_call` entries SHALL have their titles derived from `rawInput` when the stored title is only the tool kind name. `tool_call_update` completed events SHALL propagate `title` and `rawInput` to the existing entry.

#### Scenario: Page loads after plan stage completes
- **WHEN** the user navigates to an issue detail page after the plan stage has completed
- **THEN** SessionTimeline fetches all workflow_log entries and reconstructs rounds by grouping events between consecutive `user_message_chunk` entries

#### Scenario: No workflow_log entries exist
- **WHEN** the user views a draft issue with no agent activity
- **THEN** SessionTimeline shows "No agent activity yet" placeholder

#### Scenario: Historical tool call title derived from rawInput
- **WHEN** a `tool_call` log entry has `kind: 'read'`, `title: 'read'`, and `rawInput: '{"file_path": "packages/cli/src/server.ts"}'`
- **THEN** the reconstructed ToolCallEntry displays "server.ts" as the context label

#### Scenario: Historical thought text separated from message text
- **WHEN** workflow_log contains `agent_thought_chunk` events mixed with `agent_message_chunk` events
- **THEN** thought text is reconstructed into a separate `thoughtText` field on the round, and message text goes into `agentText`
