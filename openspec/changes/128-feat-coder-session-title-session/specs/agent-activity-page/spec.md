## MODIFIED Requirements

### Requirement: Active session card display
The ActivityPage SHALL display running coder sessions as cards in an "Active" section. Each card SHALL show: status icon (running), issue number + title, session title (if present) or task description as fallback, stage label (Build/Plan/Review), model name, running duration (live-updated), last 3 activity previews, and task progress bar. The session's `title` field SHALL be used as the primary label when available.

#### Scenario: Active session card content
- **WHEN** a coder session is running on issue #42 with `title: "T-001: Implement login API"`
- **THEN** the card displays: issue number `#42`, issue title, session title "T-001: Implement login API" as the primary label, stage label, model name, elapsed time updating every second, last 3 activity previews, and a progress bar

#### Scenario: Active session card without title falls back to taskDescription
- **WHEN** a coder session is running on issue #42 with `title: null` and task description "Implement login API"
- **THEN** the card displays: issue number `#42`, issue title, truncated task description as the label, stage label, model name, elapsed time updating every second, last 3 activity previews, and a progress bar

#### Scenario: Running duration live update
- **WHEN** a session has been running for 5 minutes and 30 seconds
- **THEN** the card displays `5m 30s`
- **AND** the duration updates every second without page refresh

#### Scenario: Activity previews from SSE events
- **WHEN** a `coder_tool_call` event arrives for a running session with title "Edit file src/auth.ts"
- **THEN** the card's activity preview area shows "Edit file src/auth.ts" as the newest entry
- **AND** only the last 3 previews are retained

#### Scenario: Activity preview from text chunk
- **WHEN** a `coder_text_chunk` event arrives for a running session
- **THEN** the card's activity preview shows a truncated text snippet (max 80 characters) from the chunk
- **AND** consecutive text chunks are merged into a single preview entry until a tool call arrives

#### Scenario: Task progress bar
- **WHEN** a `ralph_task_update` event arrives with `{ completed: 2, total: 5 }`
- **THEN** the card's progress bar shows 40% filled
- **AND** displays text "2/5 tasks"

#### Scenario: Progress bar absent when no task data
- **WHEN** a running session has no `ralph_task_update` events (e.g., Plan/Review stage)
- **THEN** no progress bar is displayed on the card
