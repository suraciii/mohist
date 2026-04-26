## ADDED Requirements

### Requirement: ACP session process exit logged unconditionally with phase

Both `runAcpSession` and `createAcpConnection` SHALL write an `acp_session_process_exit` workflow log entry on every `proc.on("exit")` event, regardless of `initialized` state. The log entry SHALL include `exitCode`, `phase` (either `"init"` or `"running"`), `mode` (`"single"` for runAcpSession, `"multi-round"` for createAcpConnection), and `timestamp`.

The existing init-phase rejection logic (calling `rejectOnSpawn`/`rejectOnInit` when `!initialized && code !== 0`) SHALL remain unchanged.

#### Scenario: Process exits during init phase

- **WHEN** the opencode ACP subprocess exits before initialization completes
- **THEN** the system SHALL write `acp_session_process_exit` with `phase: "init"`, `exitCode`, `mode`, and `timestamp`
- **AND** the existing rejection/error handling SHALL execute unchanged

#### Scenario: Process exits during running phase

- **WHEN** the opencode ACP subprocess exits after initialization succeeded (`initialized === true`)
- **THEN** the system SHALL write `acp_session_process_exit` with `phase: "running"`, `exitCode`, `mode`, and `timestamp`
- **AND** no rejection logic SHALL execute (the session's normal completion/error path handles this)

#### Scenario: Both runAcpSession and createAcpConnection log exits

- **WHEN** a process exit occurs in either `runAcpSession` or `createAcpConnection`
- **THEN** the log entry SHALL include `mode: "single"` (runAcpSession) or `mode: "multi-round"` (createAcpConnection) respectively
