## MODIFIED Requirements

### Requirement: canonical pipeline stage model (REQ-001)
The system SHALL use a single canonical pipeline stage model shared by backend and frontend: `backlog`, `plan`, `build`, `check`, `integrate`, `done`.

#### Scenario: Deprecated stage values are not legal pipeline stages
- **WHEN** stage values are validated, compared, or serialized for issue pipeline state
- **THEN** `draft` and `explore` are not accepted as legal pipeline stage values
- **AND** `backlog`, `plan`, `build`, `check`, `integrate`, and `done` remain supported

### Requirement: canonical stage order and transitions (REQ-002)
The system SHALL order and transition pipeline stages using the real user-visible flow.

#### Scenario: Stage order matches the real pipeline
- **WHEN** the system compares stage order or computes forward progression
- **THEN** it uses `backlog -> plan -> build -> check -> integrate -> done`

#### Scenario: Pipeline start enters plan from backlog
- **WHEN** an issue is created and then started
- **THEN** it begins in `backlog`
- **AND** starting the pipeline advances it to `plan`

#### Scenario: Check approval advances into integrate
- **WHEN** Check is approved
- **THEN** the issue advances to `integrate`
- **AND** it does not skip directly to `done`

#### Scenario: Recovery loops do not depend on deprecated stages
- **WHEN** the system validates a recovery or retry path
- **THEN** any allowed non-linear transition uses real pipeline stages such as `check -> build` or `integrate -> build`
- **AND** no legality check depends on `draft` or `explore`
