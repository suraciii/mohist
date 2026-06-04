# OpenSpec Capability: reopen-resume

### Requirement: recovery-verbs-contract

Issue recovery verbs SHALL be intent-specific. `reopen` SHALL only reopen a closed issue, while `resume` SHALL recover paused or interrupted work without changing stage or clearing checkpoints.

#### Scenario: Reopen closed issue

- **WHEN** the user invokes reopen for an issue with status `closed`
- **THEN** the system sets the issue status to `active`
- **AND** the current stage remains unchanged
- **AND** the system does not auto-reset the issue to draft or backlog

#### Scenario: Reopen rejected for non-closed issue

- **WHEN** the user invokes reopen for an issue with status `blocked`, `paused`, or `interrupted`
- **THEN** the request is rejected
- **AND** the error explains that reopen is only for closed issues

#### Scenario: Resume paused issue

- **WHEN** the user invokes resume for an issue with status `paused`
- **THEN** the system sets the issue status to `active`
- **AND** the current stage remains unchanged
- **AND** existing checkpoints are preserved

#### Scenario: Resume interrupted issue

- **WHEN** the user invokes resume for an issue with status `interrupted`
- **THEN** the system sets the issue status to `active`
- **AND** the current stage remains unchanged
- **AND** existing checkpoints are preserved

#### Scenario: Resume rejected for failed issue

- **WHEN** the user invokes resume for an issue whose current problem is a failed or needs-action state rather than paused/interrupted recovery
- **THEN** the request is rejected
- **AND** the error directs the user to retry, rerun, or rewind instead of resume
