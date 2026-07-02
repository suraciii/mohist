# OpenSpec Capability: workflow-definition

## MODIFIED Requirements

### Requirement: REQ-WD-002 Integrate uses the standard task/check stage contract

The Integrate stage SHALL execute deterministic integration work as standard WorkflowRun tasks and SHALL run final verification as a read-only WorkflowRun check. Integrate ordering, task failure handling, merge delivery metadata, freeze behavior, and post-merge health failure handling SHALL be decided by StageRun rather than by runner-local step state.

#### Scenario: Integrate runs tasks before checks

- **WHEN** an issue enters Integrate
- **THEN** the stage SHALL execute `integrate:archive-change`, `integrate:merge`, and `integrate:push` as ordered StageRun tasks
- **AND** it SHALL run `health:integrate` only after those tasks succeed

#### Scenario: Integrate failure stays local

- **WHEN** `integrate:archive-change`, `integrate:merge`, or `integrate:push` fails
- **THEN** later Integrate tasks and checks SHALL NOT run
- **AND** the issue SHALL remain associated with Integrate failure evidence

#### Scenario: Post-merge health cannot auto-fix

- **WHEN** `health:integrate` fails after merge has completed
- **THEN** the failure SHALL be recorded as a post-merge delivery failure
- **AND** the stage SHALL NOT apply any check failure policy that would modify code after the merge freeze point

### Requirement: Stage definitions preserve existing stage semantics

The declarative definitions for Plan, Build, Check, and Integrate SHALL preserve the existing user-visible workflow semantics while moving stage differences into configuration and registries.

#### Scenario: Integrate definition preserves integration contract

- **WHEN** Integrate executes through the config-driven runner
- **THEN** it SHALL execute change archive, branch merge, and remote push as ordered stage tasks
- **AND** it SHALL run the Integrate health check only after those tasks succeed

## ADDED Requirements

### Requirement: Integrate stage declares integrate:push as the final delivery task

The built-in workflow definition SHALL declare `integrate:push` as a required Integrate stage task that runs immediately after `integrate:merge` and before any post-merge health check.

#### Scenario: Default workflow includes push task

- **WHEN** the system loads the built-in default workflow
- **THEN** the Integrate stage tasks SHALL include `integrate:push`
- **AND** `integrate:push` SHALL be ordered after `integrate:merge`
- **AND** `integrate:push` SHALL be ordered before `health:integrate`

### Requirement: Task definition schema supports optional outputs array

TaskDefinition SHALL support an optional `outputs` array that declares runtime output variables. Each entry SHALL contain a `name` that identifies the variable and a `from` selector that references a field in the action result `output` JSON. The `outputs` array SHALL be optional and SHALL NOT affect tasks that omit it.

#### Scenario: Workflow YAML declares task outputs

- **WHEN** a workflow YAML task definition includes an `outputs` array
- **THEN** the parsed TaskDefinition SHALL expose each output with `name` and `from`
- **AND** the task SHALL be considered valid when `outputs` is omitted

#### Scenario: Outputs array is optional

- **WHEN** a TaskDefinition is created without an `outputs` array
- **THEN** the task SHALL be valid
- **AND** it SHALL behave identically to a task definition before this change

#### Scenario: Output declaration validates required fields

- **WHEN** an output entry is missing `name` or `from`
- **THEN** workflow definition validation SHALL reject the entry
- **AND** the workflow SHALL NOT be dispatched until the entry is corrected
