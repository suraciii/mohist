## MODIFIED Requirements

### Requirement: Conversation turn reconstruction
Session timeline reconstruction SHALL use coder session conversation turns as the primary unit. Each persisted Mohist prompt SHALL open a new turn, and subsequent assistant text, reasoning, tool calls, errors, and recovery events SHALL attach to that turn until the next prompt or terminal session state.

#### Scenario: Prompt opens new turn
- **WHEN** the transcript assembler reads a `mohist_prompt` event
- **THEN** it starts a new conversation turn with Mohist as the user role
- **AND** the previous open turn, if any, is completed at the new prompt timestamp

#### Scenario: Assistant events attach to active turn
- **WHEN** assistant message chunks, thought chunks, tool calls, tool updates, errors, or recovery events are read after a prompt
- **THEN** they are represented as assistant parts in the active turn

#### Scenario: Legacy events without prompt
- **WHEN** historical assistant or tool events exist but no Mohist prompt was persisted
- **THEN** the transcript includes a synthetic incomplete turn with the Mohist message `Prompt was not recorded for this historical session`

#### Scenario: Terminal state closes turn
- **WHEN** a session completes, fails, times out, or is cancelled
- **THEN** the current open turn is completed at the terminal event time

### Requirement: Live and historical transcript replay
Live session viewing and historical replay SHALL use the same transcript ordering semantics. Refreshing a live session or opening a completed session SHALL reconstruct the same Mohist prompt, assistant text, reasoning, tool, and error ordering from persisted data.

#### Scenario: Refresh live session
- **WHEN** the user refreshes a live session detail page after Mohist prompts, coder text, and tool calls have streamed
- **THEN** the transcript is rebuilt in the same turn order from persisted history

#### Scenario: Completed session replay
- **WHEN** the user opens a completed coder session
- **THEN** the full transcript is displayed without relying on SSE in-memory state

#### Scenario: User scrolls away during streaming
- **WHEN** new transcript parts stream while the user is not near the bottom
- **THEN** the page does not force-scroll to the bottom
- **AND** a jump-to-bottom affordance is available
