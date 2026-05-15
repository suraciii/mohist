## MODIFIED Requirements

### Requirement: REQ-BDA-CLI-001 CLI displays base drift and rebase decisions

The CLI SHALL render base drift and rebase opportunity state from server API responses without re-deriving drift policy locally.

#### Scenario: Issue show displays drift state

- **WHEN** the user runs `mo issue show <number>` for a drifted active issue
- **THEN** the output SHALL show that the issue is behind base
- **AND** it SHALL show the rebase decision and next action

#### Scenario: Deferred rebase explains why

- **WHEN** an issue has a deferred rebase opportunity
- **THEN** CLI output SHALL show the defer reason such as running agent work or waiting for a task boundary

#### Scenario: Stale approval is not presented as actionable

- **WHEN** a Check issue has stale approval evidence due to base drift
- **THEN** CLI output SHALL NOT present the approval as currently actionable
- **AND** it SHALL guide the user to rebase or rerun Check

#### Scenario: Rebase conflict details are visible

- **WHEN** a rebase opportunity or task has conflict diagnostics
- **THEN** CLI output SHALL show conflict files, failure reason, and next action guidance
