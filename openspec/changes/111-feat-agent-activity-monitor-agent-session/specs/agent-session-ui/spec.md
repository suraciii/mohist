## ADDED Requirements

### Requirement: SSE events dispatched to activity page
The global SSE event emitter SHALL dispatch `coder_session_started`, `coder_session_completed`, `coder_text_chunk`, `coder_tool_call`, `ralph_task_update`, `ralph_loop_progress`, `agent_paused`, `question_asked`, and `question_answered` events to the ActivityPage component. The ActivityPage SHALL consume these events to update session cards, counts, and anomaly badges without full data refetch.

#### Scenario: coder_text_chunk updates activity preview on card
- **WHEN** a `coder_text_chunk` event arrives with `issueId: "5"`
- **AND** the ActivityPage has a card for issue #5 in the Active section
- **THEN** the card's activity preview area updates with a truncated text snippet

#### Scenario: coder_tool_call updates activity preview on card
- **WHEN** a `coder_tool_call` event arrives with `issueId: "5"` and `title: "Edit src/auth.ts"`
- **AND** the ActivityPage has a card for issue #5 in the Active section
- **THEN** the card's activity preview area shows "Edit src/auth.ts" as the newest entry

#### Scenario: ralph_task_update updates progress bar
- **WHEN** a `ralph_task_update` event arrives with `issueId: "5"`, `completed: 3`, `total: 5`
- **AND** the ActivityPage has a card for issue #5
- **THEN** the card's progress bar updates to 60% (3/5 tasks)

#### Scenario: coder_session_started adds new card
- **WHEN** a `coder_session_started` event arrives with `issueId: "7"` and session metadata
- **THEN** a new card appears in the Active section for issue #7
- **AND** StatusBar Active count increments

#### Scenario: coder_session_completed moves card to Recent
- **WHEN** a `coder_session_completed` event arrives with `issueId: "5"` and `status: "completed"`
- **THEN** the card for issue #5 moves from Active to Recent section
- **AND** StatusBar Active count decrements, Completed count increments

#### Scenario: agent_paused adds to Waiting section
- **WHEN** an `agent_paused` event arrives with `issueId: "3"`
- **THEN** a card appears in the Waiting section for issue #3 with "Needs Approval" label

#### Scenario: question_asked adds to Waiting section
- **WHEN** a `question_asked` event arrives with `issueId: "3"` and question text
- **THEN** a card appears in the Waiting section for issue #3 with "Question Pending" label and truncated question preview

#### Scenario: question_answered removes from Waiting section
- **WHEN** a `question_answered` event arrives for an issue that had a "Question Pending" card
- **THEN** the "Question Pending" card is removed from the Waiting section

### Requirement: Activity page uses RAF throttling for high-frequency events
The ActivityPage SHALL implement requestAnimationFrame-based throttling for `coder_text_chunk` events to prevent excessive re-renders when multiple agents stream text simultaneously.

#### Scenario: Multiple agents streaming text
- **WHEN** 3 agents emit `coder_text_chunk` events simultaneously (300+ events/second total)
- **THEN** the ActivityPage UI updates in batches (every ~100ms) instead of per-event
- **AND** no frame drops or UI jank occurs
