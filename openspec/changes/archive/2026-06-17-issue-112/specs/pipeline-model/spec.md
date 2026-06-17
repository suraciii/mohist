## MODIFIED Requirements

### Requirement: REQ-PM-007 Integrate failures stay local with visible task/check evidence

Integrate SHALL have the same visible runtime lifecycle as other runnable stages, with task failures stopping the stage locally and post-task check failures remaining visible in Integrate. A post-merge health failure SHALL be distinguished from ordinary repairable check failures because merge delivery has already occurred. Merge task failure SHALL cover push and remote-advanced failures as part of the same `integrate:merge` task; a push or remote-advanced failure SHALL NOT be exposed as a separate task failure.

#### Scenario: Task failure stops later Integrate work

- **WHEN** `integrate:spec-sync`, `integrate:archive-change`, or `integrate:merge` fails
- **THEN** later Integrate tasks and checks SHALL NOT run
- **AND** the issue SHALL remain in Integrate failure state with the failing task visible

#### Scenario: Final health failure after merge requires manual intervention

- **WHEN** `health:integrate` fails after merge succeeds
- **THEN** the issue SHALL show that merge already happened
- **AND** WorkflowRun SHALL fail with post-merge delivery failure evidence rather than scheduling an automatic fix task

#### Scenario: Push failure is part of merge task failure

- **WHEN** `integrate:merge` fails because the fast-forward push was rejected due to a remote-advanced race
- **THEN** the failure SHALL be reported as an `integrate:merge` task failure
- **AND** it SHALL NOT be reported as a separate push task failure
- **AND** the failure evidence SHALL include the merge phase classification `push` and the retry attempt count

#### Scenario: Rebase conflict failure is part of merge task failure

- **WHEN** `integrate:merge` fails because rebase conflicts could not be resolved
- **THEN** the failure SHALL be reported as an `integrate:merge` task failure
- **AND** the failure evidence SHALL include the merge phase classification `rebase-conflict` and the list of unresolved conflict files
