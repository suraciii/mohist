## MODIFIED Requirements

### Requirement: REQ-WR-005 Integrate runtime work is first-class WorkflowRun state

Integrate stage progress SHALL be represented in `WorkflowRun` using standard task and check entities rather than runner-local step state only. The Integrate `StageRun` SHALL expose the ordered tasks `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge`, plus the final verification check `health:integrate`.

#### Scenario: Integrate stage is seeded with visible work

- **WHEN** an issue starts or resumes with an active `WorkflowRun`
- **THEN** the Integrate `StageRun` SHALL contain pending tasks `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge` in execution order
- **AND** it SHALL contain a pending check `health:integrate`

#### Scenario: Integrate execution updates WorkflowRun tasks and checks

- **WHEN** Integrate executes or fails any of its ordered tasks
- **THEN** the corresponding `workflow_tasks` row SHALL reflect the latest status, attempts, duration, and output
- **AND** final verification SHALL update `workflow_checks` using check identity `health:integrate`
