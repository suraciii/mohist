## MODIFIED Requirements

### Requirement: REQ-WD-002 Integrate uses the standard task/check stage contract

The Integrate stage SHALL execute deterministic integration work as standard WorkflowRun tasks and SHALL run final verification as a read-only WorkflowRun check. Integrate ordering, task failure handling, merge delivery metadata, freeze behavior, and post-merge health failure handling SHALL be decided by StageRun rather than by runner-local step state.

#### Scenario: Integrate runs tasks before checks

- **WHEN** an issue enters Integrate
- **THEN** the stage SHALL execute `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge` as ordered StageRun tasks
- **AND** it SHALL run `health:integrate` only after those tasks succeed

#### Scenario: Integrate failure stays local

- **WHEN** `integrate:spec-sync`, `integrate:archive-change`, or `integrate:merge` fails
- **THEN** later Integrate tasks and checks SHALL NOT run
- **AND** the issue SHALL remain associated with Integrate failure evidence

#### Scenario: Post-merge health cannot auto-fix

- **WHEN** `health:integrate` fails after merge has completed
- **THEN** the failure SHALL be recorded as a post-merge delivery failure
- **AND** the stage SHALL NOT apply any check failure policy that would modify code after the merge freeze point
