## MODIFIED Requirements

### Requirement: SessionHeader component renders session metadata bar
The `SessionHeader` component SHALL render a compact metadata bar for a session showing: status icon, label, coder type, model, start time, duration, and an expand/collapse chevron. The label SHALL be derived using the priority chain: `session.title` > taskId parsed from `executionId` > stage name > first 24 chars of `taskDescription`. It SHALL accept props: `session: CoderSessionItem`, `isExpanded: boolean`, `onClick: () => void`.

#### Scenario: Running session header with title
- **WHEN** SessionHeader renders with `session.status === 'running'`, `session.title === "Plan stage"`, and `isExpanded === true`
- **THEN** it shows a pulsing status icon, "Plan stage" as the label, coder/model info, live duration, and a downward chevron

#### Scenario: Completed session header with title
- **WHEN** SessionHeader renders with `session.status === 'completed'`, `session.title === "T-003: Add tests"`, and `isExpanded === false`
- **THEN** it shows a checkmark icon, "T-003: Add tests" as the label, coder/model info, fixed duration, and a rightward chevron

#### Scenario: Session header without title falls back to taskId
- **WHEN** SessionHeader renders with `session.title === null` and `session.executionId === "build-127-T-004"`
- **THEN** the label displays "T-004" parsed from executionId

#### Scenario: Session header without title or taskId falls back to stage
- **WHEN** SessionHeader renders with `session.title === null`, no parseable executionId, and `session.stage === "Plan"`
- **THEN** the label displays "Plan"

### Requirement: useCoderSessions hook provides session list with live updates
The `useCoderSessions(issueNumber)` hook SHALL provide `{ sessions, isLoading }`. Initial data SHALL be fetched from `GET /api/issues/:number/coder-sessions`. Live updates SHALL be applied from SSE events:
- `coder_session_started`: insert new session into the list (including `title` from event payload)
- `coder_session_completed`: update matching session's status, completedAt, and duration
- Running sessions' durations SHALL update every second via a local timer

#### Scenario: Initial load fetches all sessions
- **WHEN** the component mounts and the hook executes
- **THEN** it calls `GET /api/issues/:number/coder-sessions` and returns all sessions ordered by `createdAt`

#### Scenario: New session started event arrives with title
- **WHEN** a `coder_session_started` SSE event arrives for the current issue with `title: "T-002: Fix auth"`
- **THEN** a new session entry appears in the sessions array with `title: "T-002: Fix auth"` and other metadata

#### Scenario: Session completed event arrives
- **WHEN** a `coder_session_completed` SSE event arrives for the current issue
- **THEN** the matching session's status updates to the event's status and `completedAt` is set

#### Scenario: Duration timer ticks for running sessions
- **WHEN** the sessions array contains at least one session with `status: 'running'`
- **THEN** a 1-second interval timer updates the displayed duration for all running sessions
- **AND** the timer is cleaned up when all sessions are no longer running
