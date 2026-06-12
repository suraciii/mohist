## ADDED Requirements

### Requirement: Task definitions declare workflow artifact capture metadata
Workflow definitions SHALL support task-level `artifacts.files` as workflow-owned metadata that declares files or directories to record as WorkflowArtifacts after task execution. `artifacts.files` MUST remain separate from action `with` input and MUST NOT be passed to actions as generic input.

#### Scenario: Task declares artifact files
- **WHEN** a workflow task declares `artifacts.files` entries with paths
- **THEN** the workflow definition parser SHALL preserve those paths as task artifact capture metadata
- **AND** the runtime SHALL make the declarations available to the runner for upload after task execution

#### Scenario: Artifact metadata is not action input
- **WHEN** a task contains both `with` and task-level `artifacts`
- **THEN** Mohist SHALL pass `with` to the action as action input
- **AND** it SHALL NOT merge `artifacts.files` into the action input payload

### Requirement: Action expectations remain separate from artifact declarations
Workflow definitions SHALL continue to treat `with.expect.files` and `with.expect.markers` as action-level completion requirements for actions such as `mohist/acp-agent`. Mohist MUST NOT infer recorded WorkflowArtifacts from `with.expect.files`.

#### Scenario: Expected file is not automatically recorded
- **WHEN** a task declares `with.expect.files` but does not declare matching `artifacts.files`
- **THEN** the action MAY use the expected file for completion validation
- **AND** Mohist SHALL NOT record a WorkflowArtifact solely because the file was listed in `with.expect.files`

#### Scenario: Same path can serve both purposes
- **WHEN** a task declares the same path in `with.expect.markers` and `artifacts.files`
- **THEN** the marker expectation SHALL validate action completion
- **AND** the artifact declaration SHALL independently request WorkflowArtifact capture

### Requirement: Declared artifact files are required in the first version
Every path declared in task-level `artifacts.files` SHALL be required for that task run in the first version. If the runner cannot capture or upload a declared artifact, the task SHALL fail through normal task failure handling.

#### Scenario: Missing declared artifact fails task
- **WHEN** a task finishes execution but a declared `artifacts.files` path does not exist or cannot be uploaded
- **THEN** the runner or server SHALL report task failure through the normal task result path
- **AND** the workflow SHALL NOT treat the task as completed with a missing artifact warning

#### Scenario: Declared directory is required as a collection
- **WHEN** a task declares a directory path in `artifacts.files`
- **THEN** the runner SHALL capture that directory as one required artifact collection
- **AND** failure to capture the directory within safety limits SHALL fail the task through normal task failure handling
