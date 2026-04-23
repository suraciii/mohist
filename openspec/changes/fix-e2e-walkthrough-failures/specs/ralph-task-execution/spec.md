## MODIFIED Requirements

### Requirement: ACP spawn uses absolute path
The system SHALL use an absolute path to the opencode binary when spawning ACP sessions, resolving it via `resolveOpencodeBinPath()` when `opencodeBinPath` is not provided in options.

#### Scenario: opencodeBinPath not provided
- **WHEN** `runAcpSession` or `runMultiRoundAcpSession` is called without `opencodeBinPath`
- **THEN** the system SHALL call `resolveOpencodeBinPath()` to obtain the absolute path
- **AND** use that path to spawn the subprocess
- **AND** NOT fall back to bare `'opencode'` which depends on PATH

#### Scenario: opencodeBinPath provided
- **WHEN** `opencodeBinPath` is explicitly provided in options
- **THEN** the system SHALL use it directly (no change from current behavior)
