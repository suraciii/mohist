## MODIFIED Requirements

### Requirement: REQ-PM-007 Integrate failures stay local with visible task/check evidence

Integrate SHALL have the same visible runtime lifecycle as other runnable stages, with task failures stopping the stage locally and check failures remaining in Integrate with visible evidence. The workflow SHALL NOT treat Integrate as an opaque "running" step once task/check state is available.

#### Scenario: Task failure stops later Integrate work

- **WHEN** `integrate:spec-sync`, `integrate:archive-change`, or `integrate:merge` fails
- **THEN** later Integrate tasks and checks SHALL NOT run
- **AND** the issue SHALL remain in Integrate failure state with the failing task visible

#### Scenario: Final health failure remains an Integrate check failure

- **WHEN** `health:integrate` fails after merge succeeds
- **THEN** the issue SHALL remain in Integrate with visible failed check evidence
- **AND** any configured auto-fix or recheck behavior SHALL occur inside Integrate rather than bypassing the shared stage framework
