# OpenSpec Capability: workflow-definition

## ADDED Requirements

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
