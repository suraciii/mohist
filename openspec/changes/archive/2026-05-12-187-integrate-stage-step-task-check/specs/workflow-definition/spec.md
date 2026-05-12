## MODIFIED Requirements

### Requirement: REQ-WD-002 Integrate uses the standard task/check stage contract

The Integrate stage SHALL execute deterministic integration work as standard stage tasks and SHALL run final verification as a post-task check through the shared `BaseStageRunner` lifecycle. Integrate SHALL use the same task execution, check classification, and check-failure handling contract as other runnable stages.

#### Scenario: Integrate runs tasks before checks

- **WHEN** an issue enters Integrate
- **THEN** the stage SHALL execute `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge` as ordered stage tasks
- **AND** it SHALL run `health:integrate` only after those tasks succeed

#### Scenario: Integrate health failure uses standard check handling

- **WHEN** `health:integrate` fails
- **THEN** the failure SHALL be recorded as a standard check result
- **AND** the stage SHALL apply configured `CheckFailurePolicy` behavior for that check instead of runner-local failure handling
