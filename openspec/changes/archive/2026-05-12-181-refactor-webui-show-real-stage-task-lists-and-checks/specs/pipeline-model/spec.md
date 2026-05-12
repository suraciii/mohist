## MODIFIED Requirements

### Requirement: REQ-PM-001 Stage task check boundaries are explicit

Pipeline stages SHALL present one canonical user-visible task list and one separate user-visible check list per stage. Every visible task SHALL represent a real workflow execution unit, and repairs triggered by failed checks SHALL remain tasks in that same list rather than becoming a second task category or a check surrogate.

#### Scenario: Placeholder rows are not visible tasks

- **WHEN** a stage contains stored placeholder rows that do not correspond to real executable workflow work
- **THEN** those rows SHALL NOT appear in the user-visible stage task list
- **AND** the stage SHALL instead show only real workflow tasks that executed, are executing, or were actually added for retry or repair

#### Scenario: Runtime repair stays in the same task list

- **WHEN** a check failure causes a repair task such as `repair-plan-artifacts`, `fix-build-health`, `fix-review-findings`, `repair-merge`, or rebase-related work to be added
- **THEN** that repair SHALL appear in the same stage task list as the original stage work
- **AND** the task MAY include explanation metadata describing why it was added

#### Scenario: Checks remain distinct from tasks

- **WHEN** a stage reports task progress, check results, and approval state
- **THEN** tasks SHALL be shown in the stage task list
- **AND** checks SHALL be shown in a separate check list
- **AND** approval SHALL remain decision state rather than becoming a synthetic task
