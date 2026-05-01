## ADDED Requirements

### Requirement: Activity page route and layout
The WebUI SHALL provide an `/activity` route rendering an `ActivityPage` component. The page SHALL be responsive and usable on mobile viewports.

#### Scenario: Navigate to activity page
- **WHEN** user navigates to `/activity`
- **THEN** the ActivityPage component renders with StatusBar at the top, followed by session card groups

#### Scenario: Mobile responsive layout
- **WHEN** user opens `/activity` on a viewport narrower than 768px
- **THEN** session cards stack vertically in a single column
- **AND** StatusBar counts remain visible without horizontal scroll

### Requirement: StatusBar displays global counters
The ActivityPage SHALL display a StatusBar at the top showing real-time aggregate counts across all issues in the current project.

#### Scenario: StatusBar shows correct counts
- **WHEN** there are 3 running sessions, 2 issues waiting for approval, 5 completed sessions, and 1 failed session
- **THEN** StatusBar displays: Active: 3, Waiting: 2, Completed: 5, Failed: 1
- **AND** displays slot usage as `{activeAgents.length}/{maxConcurrentAgents} slots used`

#### Scenario: StatusBar updates in real-time
- **WHEN** a new coder session starts while the user is viewing the Activity page
- **THEN** the Active count increments without page refresh
- **AND** slot usage updates accordingly

#### Scenario: No active sessions
- **WHEN** there are no running, waiting, completed, or failed sessions
- **THEN** StatusBar displays all counts as 0
- **AND** slot usage shows `0/{maxConcurrentAgents} slots used`

### Requirement: Active session card display
The ActivityPage SHALL display running coder sessions as cards in an "Active" section. Each card SHALL show: status icon (running), issue number + title, task description, stage label (Build/Plan/Review), model name, running duration (live-updated), last 3 activity previews, and task progress bar.

#### Scenario: Active session card content
- **WHEN** a coder session is running on issue #42 with task description "Implement login API"
- **THEN** the card displays: issue number `#42`, issue title, truncated task description, stage label, model name, elapsed time updating every second, last 3 activity previews, and a progress bar

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

### Requirement: Click-through to session detail
Clicking an active session card SHALL navigate to the full conversation view for that session.

#### Scenario: Click active session card
- **WHEN** user clicks a card for issue #42 session `sess-abc`
- **THEN** the browser navigates to `/issue/42` where the SessionTimeline for that session is visible

### Requirement: Waiting section displays issues needing user action
The ActivityPage SHALL display a "Waiting" section showing issues that require user intervention: issues paused at a gate (agent_paused) and issues with pending questions (question_asked).

#### Scenario: Issue waiting for approval
- **WHEN** an agent completes the Plan stage and pauses at the gate
- **THEN** the Waiting section shows a card for that issue with label "Needs Approval"
- **AND** the card displays issue number, title, and which stage is waiting

#### Scenario: Issue with pending question
- **WHEN** an agent asks a question via ask_user and is waiting for a response
- **THEN** the Waiting section shows a card for that issue with label "Question Pending"
- **AND** the card displays issue number, title, and a truncated preview of the question text

#### Scenario: Issue resolved from waiting
- **WHEN** user approves or answers a question for an issue in the Waiting section
- **THEN** the card is removed from the Waiting section without page refresh
- **AND** a new card may appear in the Active section if the agent resumes

### Requirement: Recent section displays completed and failed sessions
The ActivityPage SHALL display a "Recent" section showing recently completed or failed sessions.

#### Scenario: Completed session in Recent section
- **WHEN** a coder session completes successfully
- **THEN** the session card moves from Active to Recent section
- **AND** the card shows completed status icon, issue number, title, and completion time

#### Scenario: Failed session in Recent section
- **WHEN** a coder session fails
- **THEN** the session card moves from Active to Recent section
- **AND** the card shows failed status icon and a brief error indicator

#### Scenario: Recent section order
- **WHEN** multiple sessions are in the Recent section
- **THEN** sessions are ordered by completion time, most recent first

### Requirement: Anomaly detection badges
The ActivityPage SHALL display anomaly warning badges on session cards when rule-based conditions are met. Badges SHALL be visually prominent but not interfere with card readability.

#### Scenario: Session running over 30 minutes
- **WHEN** a running session's elapsed time exceeds 30 minutes
- **THEN** the card displays a warning badge with text like "Running >30min"

#### Scenario: Session idle over 5 minutes
- **WHEN** a session has status=running but last activity was more than 5 minutes ago
- **THEN** the card displays a warning badge with text like "No activity >5min"

#### Scenario: Pending question unanswered over 10 minutes
- **WHEN** a question has been pending for more than 10 minutes without an answer
- **THEN** the corresponding Waiting card displays a warning badge with text like "Unanswered >10min"

#### Scenario: Multiple anomalies on same card
- **WHEN** a session is both running over 30 minutes AND idle over 5 minutes
- **THEN** the card displays both warning badges

#### Scenario: Anomaly resolves
- **WHEN** a session that had an idle warning receives new activity
- **THEN** the idle warning badge is removed from the card

### Requirement: Real-time updates via SSE
The ActivityPage SHALL subscribe to existing SSE events and update cards in real-time without page refresh.

#### Scenario: New session starts
- **WHEN** a `coder_session_started` event is received
- **THEN** a new card appears in the Active section
- **AND** StatusBar Active count increments

#### Scenario: Session completes
- **WHEN** a `coder_session_completed` event is received
- **THEN** the card moves from Active to Recent section
- **AND** StatusBar Active count decrements and Completed count increments

#### Scenario: Agent pauses at gate
- **WHEN** an `agent_paused` event is received
- **THEN** a card appears in the Waiting section with "Needs Approval" label

#### Scenario: Question asked
- **WHEN** a `question_asked` event is received
- **THEN** a card appears in the Waiting section with "Question Pending" label

#### Scenario: Tool call updates activity preview
- **WHEN** a `coder_tool_call` event is received for an active session
- **THEN** the corresponding card's activity preview is updated with the tool call title

#### Scenario: Task progress update
- **WHEN** a `ralph_task_update` event is received for an active session
- **THEN** the corresponding card's progress bar is updated with the new completed/total values

#### Scenario: Ralph loop progress
- **WHEN** a `ralph_loop_progress` event is received
- **THEN** the corresponding card updates its progress display if applicable
