## MODIFIED Requirements

### Requirement: REQ-BDA-001 Active candidates expose base drift state

Mohist SHALL evaluate active issue candidates against the current project base branch resolved from the issue's project repository reference and expose whether the candidate is aligned, drifted, or missing enough evidence to decide confidently. Base drift SHALL be treated as candidate relationship state, not as a workflow failure.

#### Scenario: Candidate remains aligned with base

- **WHEN** an active issue candidate's observed base position matches the current project base position
- **THEN** Mohist SHALL report `drifted = false`
- **AND** the rebase decision SHALL be `skip`

#### Scenario: Candidate is behind current base

- **WHEN** the project base branch advances after the candidate observed its base position
- **THEN** Mohist SHALL report `drifted = true`
- **AND** the drift state SHALL include observed base, current base, candidate head, and merge-base facts when available
- **AND** the workflow SHALL NOT fail solely because drift was detected

#### Scenario: Historical observation is missing

- **WHEN** an older active issue has no stored observed base position
- **THEN** Mohist SHALL derive the best available observation from existing candidate evidence or current merge-base facts
- **AND** it SHALL avoid failing the issue solely because historical base observation is incomplete

#### Scenario: Repository base branch changes after issue creation
- **WHEN** the base branch for the issue's project repository changes after the issue was created
- **THEN** Mohist SHALL evaluate drift and rebase decisions against the resolved current project repository base branch
- **AND** stale issue-owned repository snapshot data SHALL NOT override that decision

#### Scenario: Repository reference cannot be resolved for drift evaluation
- **WHEN** an active issue's repository reference cannot be resolved in the current project configuration
- **THEN** Mohist SHALL surface a repository configuration problem for the issue
- **AND** it SHALL NOT fall back to an implicit branch default to continue drift evaluation
