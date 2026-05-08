## MODIFIED Requirements

### Requirement: Session events preserve transcript display data

Coder session tracking SHALL preserve the prompt, tool, file-change, and terminal fields required to rebuild a readable transcript from persisted events.

#### Scenario: Prompt metadata is persisted

- **WHEN** Mohist sends a prompt to a coder session
- **THEN** persisted prompt data includes full prompt text, kind, title or role summary where available, output contract path where available, context files where available, and sent timestamp

#### Scenario: Tool payloads keep inferable identity fields

- **WHEN** tool start or update events are persisted or emitted
- **THEN** fields such as toolName, name, title, rawInput, rawOutput, raw output metadata, status, and ids are preserved when available

#### Scenario: Terminal status is replayable

- **WHEN** a coder session completes, fails, times out, or is cancelled
- **THEN** persisted session status and terminal timing are sufficient for the transcript API to close turns and derive user-facing state
