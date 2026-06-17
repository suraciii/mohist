## ADDED Requirements

### Requirement: Prompt-level timeout surfaces provider diagnostics

When a prompt exceeds its configured timeout budget without completing, agent runtime SHALL treat the prompt timeout as a session failure on par with the other session failure paths. It SHALL query the opencode session log for a provider error diagnostic, emit a `failed` liveness status event carrying that diagnostic, and return the diagnostic appended to the timeout error message. The failure reason for this path SHALL be `prompt_timeout`.

#### Scenario: Provider error is surfaced on prompt timeout

- **WHEN** a prompt exceeds its configured timeout budget
- **AND** the opencode session log contains a provider error diagnostic (for example `Token Plan usage limit reached`)
- **THEN** the session SHALL transition to `failed` with failure reason `prompt_timeout`
- **AND** agent runtime SHALL emit a `failed` liveness status event carrying the provider error diagnostic
- **AND** the returned error message SHALL include the provider error diagnostic

#### Scenario: No provider error on prompt timeout

- **WHEN** a prompt exceeds its configured timeout budget
- **AND** the opencode session log contains no provider error diagnostic
- **THEN** the session SHALL transition to `failed` with failure reason `prompt_timeout`
- **AND** agent runtime SHALL emit a `failed` liveness status event with no provider error diagnostic
- **AND** the returned error message SHALL NOT include a diagnostic section

### Requirement: Session failure cleanup is bounded by a hard timeout

After any session failure, agent runtime SHALL bound the post-failure cleanup — the ACP `cancel` request — by a hard timeout so a hung opencode process cannot block the runner indefinitely. On an ephemeral session (a process the runner spawned), when the cancel does not complete within the hard timeout the runner SHALL force-clean that process so the monitoring prompt can return. On a shared session, the runner SHALL NOT kill the process when the cancel hangs; the shared connection SHALL remain reusable by subsequent tasks.

#### Scenario: Ephemeral session cancel hangs

- **WHEN** a session failure triggers cleanup on an ephemeral session that the runner spawned
- **AND** the ACP `cancel` request does not complete within the configured hard timeout
- **THEN** the runner SHALL stop waiting for the cancel and force-clean the spawned process
- **AND** cleanup SHALL return within the hard timeout bound (plus any process-kill grace period)

#### Scenario: Shared session cancel hangs

- **WHEN** a session failure triggers cleanup on a shared session
- **AND** the ACP `cancel` request does not complete within the configured hard timeout
- **THEN** the runner SHALL stop waiting for the cancel
- **AND** SHALL NOT kill the shared process
- **AND** the shared connection SHALL remain usable by subsequent tasks

#### Scenario: Cancel completes promptly

- **WHEN** a session failure triggers cleanup
- **AND** the ACP `cancel` request completes within the configured hard timeout
- **THEN** the runner SHALL NOT force-clean or kill the process
