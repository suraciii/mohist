## ADDED Requirements

### Requirement: API accepts runner artifact uploads for workflow work
The HTTP API SHALL expose an internal runner upload endpoint `POST /api/workflow-runs/{workflowRunId}/work/{workId}/artifact-uploads` that accepts artifact content as `multipart/form-data`. Upload metadata SHALL include source path, content type, content hash, and size, while the server derives task run binding context from workflow run id and work id.

#### Scenario: Upload endpoint creates pending upload
- **WHEN** the runner posts multipart artifact content for a workflow run work item
- **THEN** the API SHALL create or return a pending artifact upload
- **AND** the response SHALL include an upload id for task result reporting

#### Scenario: Upload metadata omits attempt
- **WHEN** the runner uploads an artifact
- **THEN** the request SHALL NOT need to include an attempt number
- **AND** the server SHALL derive the task run from the active workflow work context

#### Scenario: Upload endpoint rejects conflicting retry
- **WHEN** the runner repeats an upload for the same workflow run, work item, task run, and path with different content hash
- **THEN** the API SHALL return a conflict response
- **AND** it SHALL NOT replace the existing pending upload

### Requirement: API accepts artifact upload ids in task result reports
The task result/report API SHALL accept `artifactUploadIds` with task results. The server SHALL validate those ids during result binding and return a structured binding failure when uploads cannot be bound to the reporting workflow run and work item.

#### Scenario: Report result includes upload ids
- **WHEN** the runner reports `{ "status": "completed", "artifactUploadIds": ["artup_..."] }`
- **THEN** the API SHALL pass the upload ids into workflow result binding
- **AND** successfully bound artifacts SHALL become visible in workflow history with the task result

#### Scenario: Report result rejects foreign upload id
- **WHEN** the runner reports an upload id from another workflow run or work item
- **THEN** the API SHALL reject the task result with a structured binding error
- **AND** it SHALL NOT partially bind the remaining uploads from that result

### Requirement: API exposes issue-scoped workflow artifact queries
The HTTP API SHALL expose issue-scoped workflow artifact endpoints for latest-by-path listing, path history, task-run filtering, and immutable content retrieval. Query responses SHALL use WorkflowArtifact or Artifact naming and SHALL NOT use Snapshot naming.

#### Scenario: Latest artifact list
- **WHEN** a client requests `GET /issues/{number}/workflow/artifacts`
- **THEN** the API SHALL return the latest artifact for each recorded path in the issue's current or recent workflow run

#### Scenario: Path history list
- **WHEN** a client requests `GET /issues/{number}/workflow/artifacts?path=review.md&history=true`
- **THEN** the API SHALL return all artifact versions for that path in production order

#### Scenario: Task-run artifact list
- **WHEN** a client requests `GET /issues/{number}/workflow/artifacts?taskRunId=ai-review.1`
- **THEN** the API SHALL return artifacts produced by that task run only

#### Scenario: Artifact content
- **WHEN** a client requests `GET /issues/{number}/workflow/artifacts/{artifactId}/content`
- **THEN** the API SHALL return the immutable recorded content for that artifact id
- **AND** it SHALL validate that the artifact belongs to the requested issue workflow context

### Requirement: API represents directory artifacts as collections
Workflow artifact API responses SHALL represent directory artifacts as one browsable artifact collection. Top-level artifact list responses SHALL not expand every contained file into separate top-level artifacts.

#### Scenario: Directory collection in latest list
- **WHEN** the latest artifact list includes a recorded `specs/` directory artifact
- **THEN** the response SHALL include one directory artifact item for `specs/`
- **AND** it SHALL provide enough metadata or links for browsing contained recorded files

#### Scenario: Directory content browsing reads recorded files
- **WHEN** a client browses a contained file under a directory artifact
- **THEN** the API SHALL serve the recorded file from artifact storage
- **AND** it SHALL NOT resolve content from the current workspace directory
