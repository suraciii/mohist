## ADDED Requirements

### Requirement: Session page prompt card reflects the real `mohist_prompt`

The dedicated session page SHALL render the user prompt card from the `mohist_prompt` event payload of the underlying session. The visible `text` of the prompt block (and the text copied to the clipboard, and the text shown when the user expands the full prompt) SHALL be the full `mohist_prompt.data.text` exactly as recorded in the session event stream. The Mohist task title, output path, and context files SHALL appear as prompt summary / subtitle / header on the same prompt block, not as the prompt body, and SHALL NOT replace `text` when the prompt block is collapsed.

#### Scenario: Full real prompt is shown and copyable

- **WHEN** the session page renders a turn whose underlying `mohist_prompt` event carries a multi-paragraph prompt body
- **THEN** the prompt card's "show full prompt" disclosure reveals that full body verbatim
- **AND** the "copy" action on the prompt card copies that same full body to the clipboard
- **AND** the short Mohist task title appears only as the prompt summary line above the body, not as the body itself

#### Scenario: Short task title does not stand in for the real prompt

- **WHEN** `session.Title` is a short task title (e.g. `Cover backend projection and progress behavior`) and the session has a recorded `mohist_prompt` event with a longer real prompt body
- **THEN** the prompt card does NOT use `session.Title` as the prompt `text`
- **AND** the task title is only used as the prompt summary / header line

### Requirement: One transcript turn per `mohist_prompt` event in event order

The session page SHALL render one transcript turn for each `mohist_prompt` event in the session event stream, in event order. Multiple `mohist_prompt` events inside a single ACP session (resumed, follow-up, recovery, or retry prompts) SHALL produce multiple turns; the page SHALL NOT collapse them into a single turn. Assistant parts (text, reasoning, tool, error, divider, terminal) SHALL be attributed to the turn whose `mohist_prompt` preceded them in the event stream.

#### Scenario: Two `mohist_prompt` events produce two turns

- **WHEN** a session event stream contains two `mohist_prompt` events with assistant activity between and after them
- **THEN** the session page renders two turns in event order
- **AND** each turn's prompt card is built from the `mohist_prompt` event that opened it
- **AND** assistant parts after the first prompt but before the second prompt appear inside the first turn

#### Scenario: Follow-up prompt opens a fresh turn

- **WHEN** a resumed / follow-up / recovery / retry `mohist_prompt` event arrives in a session that already has earlier turns
- **THEN** a new turn is opened for that prompt
- **AND** the prior turn is closed (its `completedAt` is the timestamp of the next `mohist_prompt` or the session's terminal event)

### Requirement: Assistant parts keep their emitted reasoning / text / tool interleaving

The session page SHALL preserve the natural interleaving between reasoning, text, and tool parts emitted by the agent. Reasoning that occurs between two tool calls, or text that occurs between two reasoning blocks, SHALL be visible in the same relative order on the page as it was emitted. The page SHALL NOT collapse all reasoning into one detached block at the top of a turn, and SHALL NOT collapse all text across tool boundaries into one part. Refreshing the page after a live run SHALL produce materially the same visible order as during the live run.

#### Scenario: `thought → tool → thought → text` order is preserved

- **WHEN** a turn's underlying event stream emits parts in the order `thought → tool → thought → text`
- **THEN** the page renders those parts in that same order inside the same turn's assistant part list
- **AND** the visible order does not change when the page is refreshed into the persisted replay

#### Scenario: Reasoning does not become a giant top-of-turn wall

- **WHEN** a turn contains reasoning that interleaves with tool calls
- **THEN** reasoning does not appear as a single detached block above all other assistant parts
- **AND** reasoning that appears between two tools renders between those two tools, not above them

### Requirement: Historical liveness and terminal events are visible in the transcript

The historical session transcript (loaded from `GET /api/issues/{number}/workflow/sessions/{sessionName}`) SHALL surface liveness and terminal events so refreshed pages match live pages. `agent_liveness_status` events SHALL be visible as a divider or status marker at the point in the event stream where they occurred. Terminal events (`agent_session_terminal` with `failed` / `cancelled` / `timeout` / `completed`) and recovery / interruption events SHALL be visible as the closing part of the turn they belong to, using a divider or error part with the same semantics as the live page.

#### Scenario: Failed terminal event is visible after refresh

- **WHEN** a session's event stream contains an `agent_session_terminal` event with `status: "failed"` and a `failureReason`
- **THEN** the replayed transcript shows a closing error or divider part with that failure reason on the same turn whose prompt opened the run
- **AND** the visible behavior is consistent with the live transcript at the moment the same event was first seen

#### Scenario: Liveness status change is visible after refresh

- **WHEN** a session's event stream contains `agent_liveness_status` events (e.g. transitions to `probing` and back to `running`, or to `failed`)
- **THEN** the replayed transcript includes a divider or status part at the corresponding position in the turn
- **AND** the user can identify that the session was probed, recovered, or failed without leaving the page

### Requirement: Historical sessions without `mohist_prompt` render a `legacy-missing` prompt state

When a session has no recorded `mohist_prompt` event, the session page SHALL render an explicit `legacy-missing` prompt state for that turn (e.g. a clearly labeled card stating that the prompt was not recorded for this historical session). The page SHALL NOT use the short Mohist task title, the session name, the session ID, or any other Mohist task metadata as a substitute for the real prompt text. Tool, reasoning, text, and terminal parts that do exist in the event stream SHALL still be rendered under the `legacy-missing` prompt card so the rest of the transcript remains inspectable.

#### Scenario: Legacy session shows missing-prompt state

- **WHEN** a historical session has zero `mohist_prompt` events in its event stream
- **THEN** the session page renders exactly one turn whose prompt card is in `legacy-missing` state
- **AND** the prompt body shown to the user explicitly says the prompt was not recorded
- **AND** the prompt body is not the short task title, the session name, or the session ID

#### Scenario: Legacy session still surfaces the rest of the transcript

- **WHEN** a historical session has zero `mohist_prompt` events but does have reasoning, text, tool, or terminal events
- **THEN** those parts are still rendered inside the `legacy-missing` turn as assistant parts
- **AND** the user can still see what the agent did, what it changed, and how it ended

### Requirement: Tool parts expose raw input, output, metadata, and details

The session page SHALL preserve raw tool payload fidelity. For every tool part rendered in the transcript, the raw `input`, `output`, `metadata`, and `details` (or their `coder_session_events` equivalents) SHALL be available to the tool disclosure surface so users can audit the actual payload that was sent to and returned by the tool. The summarized semantic view remains the default; raw payload access SHALL be reachable through explicit disclosure on the tool card.

#### Scenario: Raw tool input and output are inspectable

- **WHEN** a tool part in the transcript has `rawInput` and `rawOutput` recorded in the session event stream
- **THEN** the tool card's disclosure reveals those raw values
- **AND** the disclosed values match the recorded payload, not a re-rendered summary

#### Scenario: Tool metadata and details are inspectable

- **WHEN** a tool part in the transcript has `metadata` or `details` (e.g. file change metadata, diff text, subagent metadata, exit codes) recorded in the session event stream
- **THEN** the tool card's disclosure surfaces those fields
- **AND** the raw payload access does not collapse them away into the summarized view
