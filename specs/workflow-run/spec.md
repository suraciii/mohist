## ADDED Requirements

### Requirement: Task result binding records produced WorkflowArtifacts
WorkflowRun SHALL record WorkflowArtifacts for uploaded task-produced artifacts during task result binding. The binding path SHALL associate each recorded artifact with the same workflow run and task run as the reported work item before the task result becomes visible as completed.

#### Scenario: Declared artifact is recorded before completion is visible
- **WHEN** a task reports completion with upload ids for declared `artifacts.files`
- **THEN** WorkflowRun result binding SHALL create WorkflowArtifact records for the producing task run
- **AND** the completed task history SHALL expose those artifacts on that task run

#### Scenario: Dynamic uploaded artifact is recorded for same task run
- **WHEN** an action produces and uploads an artifact that was not statically declared
- **THEN** WorkflowRun result binding SHALL record it as a WorkflowArtifact for the same producing task run
- **AND** the artifact SHALL appear with the task run that produced it

#### Scenario: Missing required declared artifact fails normal task result
- **WHEN** a task has required declared artifacts but the report lacks a valid upload for one of them
- **THEN** WorkflowRun SHALL fail the task through normal task failure semantics
- **AND** later tasks, checks, approvals, and stage completion SHALL remain blocked according to existing task failure rules

### Requirement: Task runs expose produced artifacts in workflow history
WorkflowRun history SHALL expose the WorkflowArtifacts produced by each task run. Repeated task runs that record the same path SHALL keep their own artifact lists so repair and re-check loops remain explainable.

#### Scenario: Task row includes its artifact versions
- **WHEN** a client reads workflow history for a task run that produced artifacts
- **THEN** the task run representation SHALL include artifact metadata for the immutable versions produced by that task run
- **AND** those artifact links SHALL refer to recorded content, not current workspace files

#### Scenario: Repeated ai-review reports remain visible
- **WHEN** Check executes `ai-review.1`, then `fix-review-findings`, then `ai-review.2`
- **THEN** `ai-review.1` SHALL still expose its recorded `review.md`
- **AND** `ai-review.2` SHALL expose its own later recorded `review.md`
- **AND** the latest artifact query SHALL point to the `ai-review.2` version for that path

### Requirement: Artifact binding preserves WorkflowRun domain boundaries
WorkflowRun SHALL keep artifact content and infrastructure upload details outside WorkflowRun JSON state. WorkflowRun MAY reference recorded artifact metadata needed for task history, but pending upload status, storage paths, content hashes, content types, and file sizes SHALL remain persistence or read-model details.

#### Scenario: WorkflowRun does not store artifact content
- **WHEN** an artifact is bound to a task run
- **THEN** WorkflowRun SHALL NOT embed the artifact file content in its domain JSON
- **AND** content retrieval SHALL use artifact storage through the artifact query/content path

#### Scenario: Latest artifact is not domain state
- **WHEN** multiple task runs record the same artifact path
- **THEN** WorkflowRun SHALL preserve each produced artifact as task history
- **AND** latest-by-path SHALL be derived by query logic rather than stored as a separate WorkflowRun domain object
