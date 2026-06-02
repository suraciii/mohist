## ADDED Requirements

### Requirement: `mohist_prompt` events are the canonical source of truth for the user prompt

The session-detail transcript projection SHALL use the canonical `mohist_prompt` events in the session event stream as the single source of truth for the user prompt. Each `mohist_prompt` event SHALL populate one transcript turn's `user` object with the full payload fields: `role` (`"mohist"`), `text` (the full real prompt as recorded), `kind` (e.g. `task`, `followup`, `retry`, `recovery`), `sentAt` (the event's `createdAt`), `title` (from the payload), `outputPath` (from the payload when present), and `contextFiles` (from the payload when present). The transcript projection SHALL NOT use `session.Title`, `session.SessionName`, or any other session-level metadata as a substitute for `mohist_prompt.data.text`.

#### Scenario: `turns[0].user.text` equals `mohist_prompt.data.text`

- **WHEN** a session has at least one `mohist_prompt` event
- **THEN** for each such event, the corresponding `turns[i].user.text` SHALL equal the full `mohist_prompt.data.text` exactly
- **AND** `turns[i].user.sentAt` SHALL equal the event's `createdAt`
- **AND** `turns[i].user.kind` SHALL equal the event payload's `kind` (or a normalized equivalent)

#### Scenario: Short task title is not used as the prompt

- **WHEN** a session's `Title` is a short task title (e.g. `Cover backend projection and progress behavior`) and the session has a recorded `mohist_prompt` event
- **THEN** `turns[*].user.text` SHALL NOT equal that short task title
- **AND** `turns[*].user.text` SHALL equal the full `mohist_prompt.data.text`
- **AND** the short task title, if used at all in the response, appears only as `turns[*].user.summary.title`, not as `turns[*].user.text`

### Requirement: Transcript is split by `mohist_prompt` events in event order

The session-detail transcript projection SHALL split the assistant parts into one turn per `mohist_prompt` event, in the order those events appear in the session event stream. The first `mohist_prompt` event opens the first turn. Each subsequent `mohist_prompt` event closes the previous turn (its `completedAt` is the new prompt's `sentAt` or the session's terminal timestamp) and opens a new turn. Assistant parts (text, reasoning, tool, error, divider, terminal) SHALL be attributed to the most recently opened turn whose `mohist_prompt` precedes them in event order.

#### Scenario: One turn per `mohist_prompt`

- **WHEN** a session event stream contains N `mohist_prompt` events
- **THEN** the projected transcript contains exactly N turns
- **AND** the turns are returned in the same order as the `mohist_prompt` events in the event stream
- **AND** `turns.length` equals the number of `mohist_prompt` events (or one `legacy-missing` turn when the count is zero)

#### Scenario: Multi-round prompts share one real session

- **WHEN** multiple `mohist_prompt` events appear in one ACP session (resumed, follow-up, retry, or recovery rounds)
- **THEN** the transcript for that session contains multiple turns
- **AND** the session's `turnCount` metadata equals that number

### Requirement: Assistant parts preserve natural interleaving between reasoning, text, and tools

When projecting assistant parts inside a turn, the projection SHALL preserve the natural interleaving between reasoning, text, and tool parts emitted by the agent. When a new chunk type arrives while an opposite stream part is still open, the projection SHALL close the active opposite stream part before appending the new chunk type. Reasoning parts SHALL be kept inline with the surrounding tool and text activity rather than collapsed into a single detached part.

#### Scenario: `thought → tool → thought → text` order is preserved

- **WHEN** the session event stream inside a turn emits parts in the order `thought → tool → thought → text`
- **THEN** the corresponding `turn.assistant` list contains those parts in that same order
- **AND** the reasoning is not collapsed into a single part at the top of the turn

#### Scenario: Tool call closes active text/reasoning

- **WHEN** a `tool_call` or `tool_call_update` event arrives while a text or reasoning part is still open
- **THEN** the open part is closed at that event's `createdAt`
- **AND** the tool part is inserted at the new position in the part list, not after the entire text or reasoning block

### Requirement: Tool parts expose raw input, output, metadata, and details

Tool part projection SHALL expose the raw `input`, `output`, `metadata`, and `details` recorded in the session event stream so the tool disclosure surface can show the actual payload sent to and returned by the tool. The first-observed position of the tool part in the assistant part list SHALL be preserved across `tool_call` / `tool_call_update` merges. The merge SHALL NOT discard any field of the original part unless the update explicitly supersedes it, and it SHALL preserve the first-observed position regardless of update order.

#### Scenario: Raw tool payload is preserved

- **WHEN** a tool part has `input`, `output`, `metadata`, or `details` in either the `tool_call` or `tool_call_update` payload
- **THEN** the projected tool part carries those fields verbatim
- **AND** the tool disclosure surface on the UI can render them without loss

#### Scenario: First-observed position is preserved on merge

- **WHEN** a `tool_call` event opens a tool part at index K and a later `tool_call_update` arrives for the same logical tool
- **THEN** the merged tool part remains at index K in the assistant part list
- **AND** the merge updates `status`, `title`, `rawInput`, `rawOutput`, `metadata`, `details`, and `completedAt` without moving the part to a later position

### Requirement: Historical liveness and terminal events are projected into the transcript

The session-detail transcript projection SHALL include liveness and terminal events in the assistant parts of the turn they belong to. `agent_liveness_status` events SHALL be projected as a divider or status marker part at the corresponding position in the part list. Terminal events (`agent_session_terminal` with `failed`, `cancelled`, `timeout`, or `completed`) and recovery / interruption events SHALL be projected as a closing error or divider part on the most recently opened turn. The projection SHALL NOT omit these events from the persisted replay.

#### Scenario: Terminal failure closes the open turn

- **WHEN** a session's event stream contains an `agent_session_terminal` event with `status: "failed"` and a `failureReason`
- **THEN** the projected transcript for the most recently opened turn contains a closing error or divider part carrying that failure reason
- **AND** the visible replayed transcript shows that terminal state without requiring a live SSE connection

#### Scenario: Liveness transitions are visible in replay

- **WHEN** a session's event stream contains `agent_liveness_status` events (e.g. `probing`, `running`, `failed`)
- **THEN** the projected transcript includes divider or status parts at the corresponding positions in the part list
- **AND** the user can identify liveness transitions from the historical transcript alone

### Requirement: Sessions without `mohist_prompt` produce a `legacy-missing` turn

When a session has zero `mohist_prompt` events in its event stream, the session-detail transcript projection SHALL return exactly one turn whose `user.kind` is `legacy-missing` and whose `user.text` is a clearly labeled missing-prompt string (not `session.Title`, not `session.SessionName`, not `session.Id`). Any reasoning, text, tool, error, divider, or terminal events that exist in the stream SHALL still be projected as assistant parts under that `legacy-missing` turn.

#### Scenario: No `mohist_prompt` produces `legacy-missing` turn

- **WHEN** a session's event stream contains zero `mohist_prompt` events
- **THEN** the projected transcript contains exactly one turn
- **AND** that turn's `user.kind` is `legacy-missing`
- **AND** that turn's `user.text` does not equal `session.Title`, `session.SessionName`, or `session.Id`

#### Scenario: Non-prompt events still appear under the `legacy-missing` turn

- **WHEN** a session has zero `mohist_prompt` events but has reasoning, text, tool, or terminal events
- **THEN** those events are projected as assistant parts inside the single `legacy-missing` turn
- **AND** the rest of the transcript remains inspectable even when the prompt is missing
