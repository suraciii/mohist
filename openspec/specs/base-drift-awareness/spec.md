# OpenSpec Capability: base-drift-awareness

### Requirement: REQ-BDA-001 Active candidates expose base drift state

Mohist SHALL evaluate active issue candidates against the current project base branch and expose whether the candidate is aligned, drifted, or missing enough evidence to decide confidently. Base drift SHALL be treated as candidate relationship state, not as a workflow failure.

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

### Requirement: REQ-BDA-002 Rebase opportunity decisions are normalized

Mohist SHALL convert base drift facts into one rebase opportunity decision: `skip`, `suggest`, `enqueue`, `defer`, or `needs-attention`.

#### Scenario: No drift skips rebase

- **WHEN** an active issue has no base drift
- **THEN** the rebase decision SHALL be `skip`

#### Scenario: Drift with protected work defers

- **WHEN** an active issue has base drift
- **AND** the issue has running mutating work
- **THEN** the rebase decision SHALL be `defer`
- **AND** the decision SHALL include a user-readable defer reason

#### Scenario: Drift at a safe window becomes actionable

- **WHEN** an active issue has base drift
- **AND** the issue is at a safe rebase window
- **THEN** Mohist SHALL produce `suggest`, `enqueue`, or `needs-attention` according to policy
- **AND** the decision SHALL include the next action expected from Mohist or the user

### Requirement: REQ-BDA-REGRESSION-001 Drift regressions are covered

The base drift feature SHALL include regression coverage for stale Check evidence and protected Build work.

#### Scenario: Check evidence invalidated after base advances

- **WHEN** the base advances while an active Check issue has prior merge-ready and approval evidence
- **THEN** the old evidence SHALL be marked stale or invalidated
- **AND** Check approval SHALL NOT remain actionable from that stale evidence

#### Scenario: Build task is protected until boundary

- **WHEN** the base advances while an active Build task is running mutating work
- **THEN** Mohist SHALL defer rebase
- **AND** no `rebase-branch` task SHALL be appended until a safe task boundary is reached

