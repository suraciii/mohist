## ADDED Requirements

### Requirement: onBeforeKill awaited before timeout resolution
The system SHALL await the completion of the `onBeforeKill` callback before resolving the timeout signal, ensuring the `wipCommitted` result accurately reflects whether the WIP commit succeeded.

#### Scenario: Single-round session awaits onBeforeKill
- **WHEN** a single-round ACP session (`runAcpSession`) times out
- **THEN** the system calls `onBeforeKill(cwd)` and awaits its resolved value
- **AND** the `wipCommitted` field in the result reflects the actual outcome
- **AND** only after onBeforeKill completes does the timeout get signaled to the caller

#### Scenario: Multi-round session prompt timeout awaits onBeforeKill
- **WHEN** a multi-round ACP session's `prompt()` call times out
- **THEN** the system calls `onBeforeKill(cwd)` and awaits its resolved value
- **AND** the returned `AcpSessionResult.wipCommitted` reflects the actual outcome

#### Scenario: onBeforeKill failure does not prevent timeout resolution
- **WHEN** `onBeforeKill(cwd)` throws an exception
- **THEN** the exception is caught and logged
- **AND** `wipCommitted` is set to `false`
- **AND** the timeout resolution proceeds normally

#### Scenario: WIP commit success changes failure category
- **WHEN** a task times out
- **AND** `onBeforeKill` successfully creates a WIP commit (returns `true`)
- **THEN** the failure is categorized as `timeout_with_wip`
- **AND** the Ralph executor treats it as potentially resumable

#### Scenario: WIP commit failure keeps timeout category
- **WHEN** a task times out
- **AND** `onBeforeKill` returns `false` or throws
- **THEN** the failure is categorized as `timeout`
- **AND** the retry prompt does not include `wipResumeContext`