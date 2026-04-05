## MODIFIED Requirements

### Requirement: In-memory session management
The system SHALL support creating agent sessions in memory with: a unique ID, an associated issue ID, a message history (AI SDK CoreMessage[]), a creation timestamp, and a status (active/paused/closed). Sessions SHALL support adding messages. Sessions SHALL support pause/resume lifecycle transitions. Sessions are NOT persisted to SQLite in M1/M2 — server restart loses all session data.

#### Scenario: Create session
- **WHEN** a new session is created with an issue ID
- **THEN** the system SHALL generate a unique session ID and store it in memory
- **AND** the session SHALL start with status `active` and an empty message history

#### Scenario: Append message to session
- **WHEN** a message is appended to a session
- **THEN** the message SHALL be added to the session's in-memory history
- **AND** the message SHALL be available for subsequent LLM calls
- **AND** the session MUST NOT be closed (both active and paused sessions accept messages)

#### Scenario: Pause session
- **WHEN** a session is paused
- **THEN** the session status SHALL become `paused`
- **AND** the session messages SHALL be preserved
- **AND** the session SHALL be findable via `findByIssueId()`

#### Scenario: Resume session
- **WHEN** a paused session is resumed
- **THEN** the session status SHALL become `active`
- **AND** the session messages SHALL be preserved

#### Scenario: Close session
- **WHEN** a session is closed
- **THEN** the session status SHALL become `closed`
- **AND** the session SHALL NOT accept new messages (appendMessage throws)
- **AND** the session SHALL NOT be findable via `findByIssueId()`

#### Scenario: Find session by issueId
- **WHEN** `findByIssueId(issueId)` is called
- **THEN** the system SHALL return the session with matching issueId that is active or paused
- **AND** closed sessions SHALL NOT be returned
- **AND** if no matching session exists, return undefined
