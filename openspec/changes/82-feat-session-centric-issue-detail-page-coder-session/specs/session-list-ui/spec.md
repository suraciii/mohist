## ADDED Requirements

### Requirement: SessionList renders coder sessions as primary UI units
The IssueDetailPage SHALL display a SessionList component that presents each coder session as a distinct, interactive entry. Each entry SHALL show: stage/task label, coder type, model name, running duration (or total duration if completed), and status icon. Sessions SHALL be ordered by `createdAt` ascending.

#### Scenario: Issue with Plan and Build sessions
- **WHEN** the user views an issue that completed Plan (1 session) and Build (3 task sessions)
- **THEN** SessionList renders 4 session entries: Plan, T-001, T-002, T-003, each with its own metadata row

#### Scenario: Draft issue with no sessions
- **WHEN** the user views a draft issue with no coder sessions
- **THEN** SessionList shows "No sessions yet" placeholder

### Requirement: SessionList entry displays session metadata
Each session entry in SessionList SHALL display:
- **Label**: stage name for Plan/Review sessions (e.g., "Plan", "Review"), or task ID + truncated description for Build sessions (e.g., "T-001 fix-review-report")
- **Coder info**: coder type and model name (e.g., "opencode · deepseek-v4-pro")
- **Timing**: start time (formatted as HH:MM) and duration (running timer for active sessions, fixed duration for completed)
- **Status**: visual indicator — running (animated pulse), completed (checkmark), failed (error icon)

#### Scenario: Running session shows live duration
- **WHEN** a coder session is actively running with `status: 'running'`
- **THEN** the session entry shows a pulsing indicator and a live-updating duration timer (e.g., "3m 02s" incrementing)

#### Scenario: Completed session shows fixed duration
- **WHEN** a coder session has `status: 'completed'` with `completedAt` set
- **THEN** the session entry shows a checkmark and the calculated duration between `createdAt` and `completedAt` (e.g., "17m 23s")

#### Scenario: Session with model metadata
- **WHEN** a coder session was created with `model: 'glm-5.1'` and `coderType: 'opencode'`
- **THEN** the session entry displays "opencode · glm-5.1"

### Requirement: SessionDetail expands on click
When the user clicks a session entry in SessionList, a SessionDetail panel SHALL expand inline below that entry. SessionDetail SHALL render the session's activity as rounds with agent text and tool calls, powered by `useSessionTimeline(issueNumber, coderSessionId)` scoped to that single session. Clicking again SHALL collapse the detail.

#### Scenario: Expand Plan session
- **WHEN** the user clicks the Plan session entry
- **THEN** SessionDetail expands showing 5 rounds (proposal, specs, design, tasks, self-review) with their full conversation content

#### Scenario: Collapse session
- **WHEN** the user clicks an already-expanded session entry
- **THEN** the SessionDetail collapses and only the metadata row is visible

#### Scenario: Only one session expanded at a time
- **WHEN** the user clicks a different session entry while another is expanded
- **THEN** the previously expanded session collapses and the newly clicked session expands

### Requirement: Running session auto-expands with real-time content
When a session has `status: 'running'`, SessionDetail SHALL auto-expand and stream real-time content. Agent text SHALL append with typing cursor animation. Tool calls SHALL appear as they are reported. Duration SHALL tick every second.

#### Scenario: Agent text streams into running session
- **WHEN** `coder_text_chunk` events arrive for a running session while the user is viewing the page
- **THEN** the SessionDetail for that session shows the text being appended in real-time with a typing cursor

#### Scenario: Tool call appears in running session
- **WHEN** a `coder_tool_call` event arrives for a running session
- **THEN** the tool call entry appears in the SessionDetail with the tool name, status icon, and expandable details

#### Scenario: Running session auto-expands on page load
- **WHEN** the user navigates to an issue detail page that has a running session
- **THEN** the running session's SessionDetail is automatically expanded with live content

### Requirement: useCoderSessions hook provides session list with live updates
The `useCoderSessions(issueNumber)` hook SHALL provide `{ sessions, isLoading }`. Initial data SHALL be fetched from `GET /api/issues/:number/coder-sessions`. Live updates SHALL be applied from SSE events:
- `coder_session_started`: insert new session into the list
- `coder_session_completed`: update matching session's status, completedAt, and duration
- Running sessions' durations SHALL update every second via a local timer

#### Scenario: Initial load fetches all sessions
- **WHEN** the component mounts and the hook executes
- **THEN** it calls `GET /api/issues/:number/coder-sessions` and returns all sessions ordered by `createdAt`

#### Scenario: New session started event arrives
- **WHEN** a `coder_session_started` SSE event arrives for the current issue
- **THEN** a new session entry appears in the sessions array with the event's metadata (model, coderType, stage, taskDescription)

#### Scenario: Session completed event arrives
- **WHEN** a `coder_session_completed` SSE event arrives for the current issue
- **THEN** the matching session's status updates to the event's status and `completedAt` is set

#### Scenario: Duration timer ticks for running sessions
- **WHEN** the sessions array contains at least one session with `status: 'running'`
- **THEN** a 1-second interval timer updates the displayed duration for all running sessions
- **AND** the timer is cleaned up when all sessions are no longer running

### Requirement: SessionHeader component renders session metadata bar
The `SessionHeader` component SHALL render a compact metadata bar for a session showing: status icon, label, coder type, model, start time, duration, and an expand/collapse chevron. It SHALL accept props: `session: CoderSessionItem`, `isExpanded: boolean`, `onClick: () => void`.

#### Scenario: Running session header
- **WHEN** SessionHeader renders with `session.status === 'running'` and `isExpanded === true`
- **THEN** it shows a pulsing status icon, the session label, coder/model info, live duration, and a downward chevron

#### Scenario: Completed session header
- **WHEN** SessionHeader renders with `session.status === 'completed'` and `isExpanded === false`
- **THEN** it shows a checkmark icon, the session label, coder/model info, fixed duration, and a rightward chevron
