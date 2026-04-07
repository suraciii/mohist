## ADDED Requirements

### Requirement: Create explore session
The system SHALL allow creating an explore session for a given project. A session starts with a title derived from the first user message and status `active`.

#### Scenario: Create session successfully
- **WHEN** user creates an explore session for a project
- **THEN** system creates a session with generated id, project_id, status `active`, title from first message, and timestamps

#### Scenario: Create session without existing issue
- **WHEN** an explore session is created
- **THEN** the session's issue_id SHALL be null

### Requirement: Persist explore messages
The system SHALL persist all user and assistant messages in the explore_messages table with role, content, tool_calls, and timestamp.

#### Scenario: Store user message
- **WHEN** user sends a message in an explore session
- **THEN** system stores a message with role `user`, the message content, null tool_calls, and current timestamp

#### Scenario: Store assistant message with tool calls
- **WHEN** agent responds with text and tool calls
- **THEN** system stores a message with role `assistant`, the text content, and tool_calls as JSON array of {name, args, result}

### Requirement: List explore sessions
The system SHALL list all explore sessions for a project, ordered by updated_at descending.

#### Scenario: List sessions for a project
- **WHEN** user requests explore sessions for a project
- **THEN** system returns all sessions for that project ordered by updated_at desc

### Requirement: Get explore session with messages
The system SHALL return an explore session with all its messages ordered by created_at ascending.

#### Scenario: Get existing session
- **WHEN** user requests a session by id
- **THEN** system returns the session with all messages in chronological order

#### Scenario: Get non-existent session
- **WHEN** user requests a session that does not exist
- **THEN** system returns 404

### Requirement: Delete explore session
The system SHALL allow deleting an explore session and all its messages (cascade delete).

#### Scenario: Delete session with messages
- **WHEN** user deletes an explore session
- **THEN** system deletes the session and all associated messages

### Requirement: Crystallize session to issue
The system SHALL support crystallizing an explore session by associating it with a created issue.

#### Scenario: Crystallize with issue
- **WHEN** agent creates an issue from an explore session
- **THEN** system updates the session's issue_id to the new issue's id and status to `crystallized`

#### Scenario: Continue conversation after crystallize
- **WHEN** a session is crystallized and user sends another message
- **THEN** system SHALL allow the conversation to continue normally

### Requirement: Recover session after server restart
The system SHALL load explore sessions and messages from SQLite on server start, allowing users to resume previous conversations.

#### Scenario: Resume after restart
- **WHEN** server restarts and user opens an existing explore session
- **THEN** system loads all persisted messages from DB and agent can continue the conversation with full history
