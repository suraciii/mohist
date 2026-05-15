## MODIFIED Requirements

### Requirement: Approve rejects missing or stale verification

Approval-related HTTP APIs SHALL NOT advance Check approval when full verification evidence is missing, failed, disabled, malformed, or stale for the current candidate implementation.

#### Scenario: Approve rejects missing verification evidence

- **WHEN** a user approves a Check-stage issue through the API
- **AND** approval output has no passing full verification evidence
- **THEN** the API SHALL reject approval
- **AND** it SHALL return a clear error instructing the user to rerun Check verification

#### Scenario: Approve rejects stale verification evidence

- **WHEN** a user approves a Check-stage issue through the API
- **AND** verification evidence does not match the current candidate implementation, review snapshot, or merge-ready snapshot
- **THEN** the API SHALL reject approval
- **AND** it SHALL NOT advance the issue to Integrate

### Requirement: Issue API exposes Check verification failures

Issue detail APIs SHALL expose failed or missing Check full verification evidence clearly enough for CLI and Web UI consumers to show why approval is unavailable.

#### Scenario: Issue detail includes failed Check verification

- **WHEN** Check full verification fails
- **THEN** issue detail data SHALL include the failed `health:check` status and output
- **AND** the output SHALL include command, summary, duration, and log excerpt when available
