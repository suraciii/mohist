## ADDED Requirements

### Requirement: CLI reports attempt-derived recovery guidance

CLI issue status, issue show, and recovery command output SHALL report recovery guidance from the same API recovery projection used by the Web UI.

#### Scenario: CLI shows running recovery state

- **WHEN** an issue's latest attempt state is `running` with live execution evidence
- **THEN** CLI output SHALL describe the work as running
- **AND** guidance SHALL be wait or stop rather than retry

#### Scenario: CLI shows failed retry guidance

- **WHEN** an issue's latest attempt state is `failed`
- **AND** retry is an allowed action
- **THEN** CLI output SHALL present retry as available failed-work recovery

#### Scenario: CLI shows interrupted guidance

- **WHEN** an issue's latest attempt state is `interrupted`
- **THEN** CLI output SHALL distinguish interrupted work from failed work
- **AND** guidance SHALL mention resume, rerun stage, or inspect actions according to the API projection

#### Scenario: CLI agrees with API and UI fixtures

- **WHEN** the same issue fixture is rendered through API, Web UI, and CLI
- **THEN** all three surfaces SHALL agree on latest attempt state and recovery action availability
