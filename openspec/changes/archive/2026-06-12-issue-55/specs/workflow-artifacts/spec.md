## ADDED Requirements

### Requirement: WorkflowArtifact records immutable task-produced outputs
Mohist SHALL record a WorkflowArtifact as an immutable output produced by one workflow task run. Each WorkflowArtifact MUST include workflow run id, task run id, recorded source path, and recorded timestamp as its core domain facts. Mohist MUST NOT name the model, table, DTO, route, or UI concept Snapshot.

#### Scenario: Artifact identity belongs to a task run
- **WHEN** task run `ai-review.1` records `review.md`
- **THEN** the recorded WorkflowArtifact SHALL identify the producing workflow run id and task run id
- **AND** the recorded `path` SHALL be the stable business identity for that producer

#### Scenario: Rewritten paths preserve history
- **WHEN** task run `ai-review.1` records `review.md`
- **AND** task run `ai-review.2` later records `review.md`
- **THEN** both WorkflowArtifacts SHALL remain available as separate immutable versions
- **AND** the later version SHALL NOT replace or mutate the earlier artifact

### Requirement: Artifact upload uses pending records before binding
The runner SHALL upload task-produced artifact content to the server as pending artifact uploads before reporting the task result. Pending uploads SHALL remain hidden from user-visible WorkflowArtifact queries until they are bound to the producing task run during result reporting.

#### Scenario: Runner uploads pending artifact
- **WHEN** a runner uploads an artifact for `workflowRunId`, `workId`, and source `path`
- **THEN** the server SHALL store it as a pending artifact upload
- **AND** it SHALL return an upload id that can be reported with the task result
- **AND** the pending upload SHALL NOT appear in latest, history, or task-run artifact queries

#### Scenario: Retry upload is idempotent
- **WHEN** a runner retries the same upload for the same workflow run, work item, task run, and path with the same content hash
- **THEN** the server SHALL return the existing pending upload instead of creating a duplicate

#### Scenario: Conflicting retry is rejected
- **WHEN** a runner retries the same upload key with a different content hash
- **THEN** the server SHALL reject the upload as a conflicting retry
- **AND** it SHALL NOT replace the original pending upload content

### Requirement: Artifact binding creates visible WorkflowArtifacts atomically
Mohist SHALL bind reported artifact upload ids to WorkflowArtifact records during task result reporting. Binding MUST validate that every upload id belongs to the same workflow run and work item, derive the task run from the active work context, and either bind all valid uploads for that result or fail with a structured binding failure.

#### Scenario: Completed task binds uploads
- **WHEN** a task result reports status `completed` with artifact upload ids
- **THEN** Mohist SHALL validate the upload ids against the workflow run and work item
- **AND** it SHALL create immutable WorkflowArtifact records for the producing task run before the completed result becomes visible in workflow history

#### Scenario: Failed task may bind diagnostics
- **WHEN** a task result reports status `failed` with uploaded diagnostic artifacts
- **THEN** Mohist MAY bind those uploads to the failed task run
- **AND** the resulting WorkflowArtifacts SHALL be visible as outputs of that failed task run

#### Scenario: Invalid upload id fails binding
- **WHEN** a task result includes an upload id that does not belong to the same workflow run and work item
- **THEN** Mohist SHALL reject the result binding with a structured binding failure
- **AND** it SHALL NOT expose a partial set of WorkflowArtifacts from that report

### Requirement: Artifact content is stored in filesystem-backed artifact storage
Mohist SHALL store recorded artifact content in the normal filesystem outside WorkflowRun JSON. Storage paths MUST be generated or sanitized by Mohist and MUST NOT use the source artifact path directly as path segments.

#### Scenario: File artifact content is persisted
- **WHEN** a file artifact is bound into workflow history
- **THEN** Mohist SHALL persist metadata and content under generated artifact storage rooted by workflow run id, task run id, and artifact id
- **AND** the persistence or read model SHALL retain the artifact storage path for later content retrieval

#### Scenario: Source path is display metadata only
- **WHEN** an artifact source path contains nested directories or unusual characters
- **THEN** Mohist SHALL keep the original path for display, identity, and history queries
- **AND** it SHALL NOT use that source path directly as the filesystem storage location

### Requirement: Directory artifacts are recorded as browsable collections
Mohist SHALL represent a declared directory artifact as one WorkflowArtifact collection in task and latest views. Directory capture MUST preserve contained files under the artifact collection while enforcing traversal safety, file-count limits, and total-size limits.

#### Scenario: Directory appears as one artifact
- **WHEN** a task records directory artifact `specs/`
- **THEN** the workflow and latest artifact views SHALL show one artifact collection for `specs/`
- **AND** they SHALL NOT flood the top-level artifact list with every contained file

#### Scenario: Directory files are browsable
- **WHEN** a user opens a directory WorkflowArtifact collection
- **THEN** Mohist SHALL expose the recorded contained files under that immutable artifact version
- **AND** file content SHALL come from the recorded artifact storage, not from the current workspace

#### Scenario: Unsafe directory traversal is refused
- **WHEN** runner directory capture encounters symlink traversal, too many files, excessive total size, or paths escaping the workspace
- **THEN** the capture SHALL fail safely
- **AND** Mohist SHALL NOT record files outside the allowed artifact collection

### Requirement: Artifact queries expose latest, history, task-run, and content views
Mohist SHALL expose issue-scoped, workflow-specific artifact queries for latest artifacts by path, path history, artifacts produced by one task run, and immutable content retrieval by artifact id.

#### Scenario: Latest query groups by path
- **WHEN** a client requests `GET /issues/{number}/workflow/artifacts` without history
- **THEN** the response SHALL return the newest bound WorkflowArtifact for each recorded path in the current or recent workflow run

#### Scenario: Path history query returns all versions
- **WHEN** a client requests `GET /issues/{number}/workflow/artifacts?path=review.md&history=true`
- **THEN** the response SHALL return all bound WorkflowArtifact versions for that path in production order

#### Scenario: Task-run query returns produced artifacts
- **WHEN** a client requests `GET /issues/{number}/workflow/artifacts?taskRunId=ai-review.1`
- **THEN** the response SHALL return only WorkflowArtifacts produced by that task run

#### Scenario: Content query returns immutable content
- **WHEN** a client requests `GET /issues/{number}/workflow/artifacts/{artifactId}/content`
- **THEN** the response SHALL return content for that recorded artifact version
- **AND** it SHALL NOT read the current workspace file at the artifact source path

### Requirement: WorkflowArtifactRecorded is emitted only after binding
Mohist SHALL emit `WorkflowArtifactRecorded` only after an artifact upload has been successfully bound to a task run and is visible in workflow history. Mohist SHALL NOT emit a `WorkflowArtifactMissing` domain event for missing declared artifacts.

#### Scenario: Bound artifact emits event
- **WHEN** a pending upload is bound into a visible WorkflowArtifact
- **THEN** Mohist SHALL emit `WorkflowArtifactRecorded` for that artifact

#### Scenario: Missing declared artifact uses task failure
- **WHEN** a required declared artifact cannot be found, uploaded, or bound
- **THEN** the task SHALL fail through the normal task failure path
- **AND** Mohist SHALL NOT emit a `WorkflowArtifactMissing` event
