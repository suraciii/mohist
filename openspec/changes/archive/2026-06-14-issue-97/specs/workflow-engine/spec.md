# OpenSpec Capability: workflow-engine

## ADDED Requirements

### Requirement: Task result processing extracts declared outputs

The workflow engine SHALL process successful task results by extracting declared outputs from the runner-reported `WorkResult` and storing them in the WorkflowRun runtime variable store. Extraction SHALL use the declared `from` selectors and SHALL only occur when the task completes successfully.

#### Scenario: Successful task result stores declared outputs
- **WHEN** a task completes successfully
- **AND** the `WorkResult` contains captured outputs matching the task definition
- **THEN** the workflow engine SHALL store each output in the WorkflowRun runtime variable store
- **AND** the store key SHALL be `tasks.<taskDefinitionId>.outputs.<name>`

#### Scenario: Failed task result does not store outputs
- **WHEN** a task result reports failure
- **THEN** the workflow engine SHALL NOT add any runtime variables for that task
- **AND** any previously stored variables with the same task id SHALL remain unchanged

### Requirement: Dispatch variable resolution merges runtime task outputs

`MakeDispatchAsync` SHALL deep-merge the WorkflowRun runtime variable store into the dispatch variable resolution chain after dispatch injection and before final resolution. Merged runtime variables SHALL be available under the `tasks.<id>.outputs.<name>` namespace in `${{ }}` templates.

#### Scenario: Dispatch includes captured runtime variables
- **WHEN** a task is dispatched after earlier tasks have produced outputs
- **THEN** `MakeDispatchAsync` SHALL include those outputs in the resolved variables
- **AND** they SHALL be resolvable via `${{ tasks.<id>.outputs.<name> }}`

#### Scenario: Runtime variables follow existing resolution precedence
- **WHEN** a runtime variable has the same key as a project, issue, or dispatch-injected variable
- **THEN** the runtime variable SHALL take precedence over the lower-precedence source
- **AND** the existing precedence order for non-conflicting keys SHALL remain unchanged

#### Scenario: Empty runtime store does not alter resolution
- **WHEN** no task outputs have been captured
- **THEN** `MakeDispatchAsync` SHALL produce the same resolved variables as before this change
- **AND** dispatch SHALL succeed without runtime variable entries
