# OpenSpec Capability: task-output-variables

## ADDED Requirements

### Requirement: Task definitions declare runtime output variables

Task definitions SHALL optionally declare an `outputs` array that names variables to capture from a successful action result. Each entry SHALL declare a variable `name` and a JSONPath-style `from` selector that references a field in the action result `output` JSON.

#### Scenario: Task definition declares output variables
- **WHEN** a workflow task definition includes an `outputs` array
- **THEN** each entry SHALL contain a `name` and a `from` field
- **AND** the task definition SHALL remain valid when `outputs` is omitted

#### Scenario: Duplicate output names are rejected
- **WHEN** a task definition declares two output entries with the same `name`
- **THEN** workflow validation SHALL reject the definition
- **AND** the workflow SHALL NOT start until the duplicate is resolved

### Requirement: Runner captures declared outputs from successful action results

The runner SHALL parse the `output` field of a successful `ActionResult` as JSON and extract each declared output using its `from` selector. Extracted outputs SHALL be included in the `WorkResult` reported to the server. Failed or missing values SHALL be treated as absent and SHALL NOT cause the task to fail unless explicitly required.

#### Scenario: Successful task returns declared outputs
- **WHEN** a task completes successfully
- **AND** its action result `output` contains the JSON fields referenced by declared `from` selectors
- **THEN** the runner SHALL extract each value
- **AND** include the captured outputs in the `WorkResult`

#### Scenario: Failed task produces no outputs
- **WHEN** a task fails
- **THEN** the runner SHALL NOT extract or report any declared outputs
- **AND** the `WorkResult` SHALL contain no runtime variables for that task

#### Scenario: Missing output field is skipped
- **WHEN** a task completes successfully
- **AND** a declared `from` selector references a missing JSON field
- **THEN** that output SHALL be omitted from captured results
- **AND** other valid outputs SHALL still be captured

### Requirement: WorkflowRun stores runtime task output variables

WorkflowRun SHALL own a runtime variable store scoped to the workflow run. The store SHALL be keyed by `tasks.<taskDefinitionId>.outputs.<name>` and populated from captured task outputs on successful task completion. Runtime variables SHALL persist across stage transitions within the same run.

#### Scenario: Captured outputs are stored in WorkflowRun
- **WHEN** the server receives a successful `WorkResult` with captured outputs
- **THEN** WorkflowRun SHALL store each output under `tasks.<taskDefinitionId>.outputs.<name>`
- **AND** the runtime store SHALL be part of the WorkflowRun aggregate state

#### Scenario: Runtime variables persist across stages
- **WHEN** a stage completes and a new stage begins within the same WorkflowRun
- **THEN** runtime variables captured in the earlier stage SHALL remain available
- **AND** they SHALL be resolvable in the new stage

### Requirement: Runtime variables resolve through the existing template chain

The existing `${{ }}` template resolution chain SHALL include runtime task output variables. Runtime variables SHALL be deep-merged into the resolved variables after dispatch injection and before final resolution, using the key prefix `tasks.<id>.outputs.<name>`.

#### Scenario: Subsequent task references a runtime output
- **WHEN** a task with `id: proposal` captures `outputs.openspecName`
- **AND** a later task declares `with.path: ${{ tasks.proposal.outputs.openspecName }}/specs`
- **THEN** the template SHALL resolve to the captured value

#### Scenario: Runtime outputs resolve in artifact declarations
- **WHEN** a task captures an output variable
- **AND** a later task declares an artifact path using `${{ tasks.<id>.outputs.<name> }}`
- **THEN** the artifact path SHALL resolve to the captured value

#### Scenario: Missing runtime output resolves to empty
- **WHEN** a template references `tasks.<id>.outputs.<name>` that has not been captured
- **THEN** the template expression SHALL resolve to an empty value
- **AND** resolution SHALL continue without failing

### Requirement: Tasks without outputs retain existing behavior

Tasks that do not declare `outputs` SHALL behave identically to the behavior before this change. The absence of an `outputs` array SHALL NOT introduce new variable entries, alter dispatch variable resolution, or affect downstream tasks.

#### Scenario: Task without outputs has no side effects
- **WHEN** a task definition omits the `outputs` array
- **THEN** the runner SHALL NOT extract runtime outputs for that task
- **AND** no `tasks.<id>.outputs.*` keys SHALL be added to the runtime store
- **AND** dispatch variable resolution SHALL produce the same result as before this change
