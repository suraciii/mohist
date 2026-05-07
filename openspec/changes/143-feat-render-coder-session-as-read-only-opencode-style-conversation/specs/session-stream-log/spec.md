## ADDED Requirements

### Requirement: Mohist prompt persistence
The system SHALL persist every Mohist prompt sent to a coder agent as a session stream event before sending the prompt to ACP. Persisted prompt events SHALL contain the full prompt text, role `mohist`, prompt kind, sent timestamp, and available session context without truncating the prompt for storage.

#### Scenario: Prompt persisted before ACP prompt call
- **WHEN** `AgentSession.execute(prompt)` is called for a coder session
- **THEN** a `mohist_prompt` event is inserted into `session_stream_log` before the ACP prompt request is sent
- **AND** the event data contains the full prompt text and `role: "mohist"`

#### Scenario: ACP user_message_chunk absent
- **WHEN** ACP does not emit a `user_message_chunk` for a prompt
- **THEN** coder session history still contains the Mohist prompt from the persisted `mohist_prompt` event

#### Scenario: Prompt kind recorded
- **WHEN** the prompt is known to be initial, task, retry, follow-up, or recovery/resume input
- **THEN** the persisted prompt event records the corresponding prompt kind
- **AND** unknown prompt kinds default to a safe task-like kind rather than being omitted

### Requirement: Session lifecycle metadata remains trustworthy
Coder session lifecycle metadata SHALL distinguish running sessions from terminal sessions. A running session SHALL NOT have a completed timestamp, and terminal sessions SHALL set completion time only for completed, failed, timeout, or cancelled statuses.

#### Scenario: Running session status update
- **WHEN** a coder session is still running
- **THEN** its `completedAt` value is null

#### Scenario: Terminal session status update
- **WHEN** a coder session transitions to completed, failed, timeout, or cancelled
- **THEN** its `completedAt` value is set to the terminal transition time
