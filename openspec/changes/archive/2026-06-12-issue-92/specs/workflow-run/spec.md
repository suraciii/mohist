## MODIFIED Requirements

### Requirement: REQ-WR-001 Starting an issue creates a WorkflowRun

Starting an issue SHALL create or reuse one active WorkflowRun aggregate bound to the issue id and issue number only after the Issue is start eligible. The aggregate SHALL derive its first stage from its ordered stage definition, create ordered StageRuns for `plan`, `build`, `check`, and `integrate`, seed static Plan and Integrate task/check state, and start the first StageRun without using `Issue.stage` as the state-machine decision source. WorkflowRun startup SHALL obtain repository metadata from the selected repository's `gitUrl` and `baseBranch`, prepare a runner-owned workflow workspace, and SHALL NOT depend on project or repository local path fields.

#### Scenario: Start creates aggregate-rooted run

- **WHEN** an issue is started
- **AND** the issue is start eligible
- **THEN** the system SHALL create or reuse one active WorkflowRun for that issue
- **AND** the WorkflowRun SHALL have `status = running` and `currentStage` equal to the first configured runnable stage
- **AND** the first StageRun SHALL be running
- **AND** issue stage/status updates SHALL be projections of the WorkflowRun decision
- **AND** the run SHALL have a runner-managed `workspace.path` as its only local execution directory

#### Scenario: Start is idempotent for active run

- **WHEN** start or resume code encounters an issue that already has a non-terminal active WorkflowRun
- **THEN** it SHALL reuse that WorkflowRun
- **AND** it SHALL NOT create a duplicate active run for the same issue
- **AND** it SHALL continue to use the existing workflow workspace for that run

#### Scenario: Waiting prerequisite prevents WorkflowRun creation

- **WHEN** start-pipeline execution evaluates Issue #201
- **AND** Issue #201 is waiting for prerequisite Issue #200 to be delivered
- **THEN** the system SHALL NOT create or start a WorkflowRun for Issue #201
- **AND** the system SHALL NOT create an agent session for Issue #201
- **AND** the waiting condition SHALL be recorded as start eligibility state rather than workflow failure

## ADDED Requirements

### Requirement: Workflow dispatch exposes only workspace execution path
Workflow dispatch variables SHALL expose `workspace.path` as the only local execution directory. Dispatch variables MAY expose repository metadata such as `repository.gitUrl` and `repository.baseBranch`, but SHALL NOT expose `project.path`, `project.effectivePath`, `repository.path`, `repository.remote`, `repository.resolvedPath`, repository cache paths, or user checkout paths.

#### Scenario: Dispatch variables include repository metadata
- **WHEN** WorkflowRun prepares task, check, repair, merge, or approval work
- **THEN** the dispatch context SHALL include `repository.gitUrl`, `repository.baseBranch`, and `workspace.path`
- **AND** every work item SHALL receive `workspace.path` as the execution boundary

#### Scenario: Dispatch variables omit external checkout paths
- **WHEN** dispatch variables are serialized for runner work
- **THEN** they SHALL NOT include project path, repository path, resolved path, repository cache path, or user checkout path fields
- **AND** workflow templates SHALL NOT be able to select those paths as work directories

### Requirement: Workflow runtime records workspace identity
WorkflowRun state SHALL track the runner-created workflow workspace needed to resume and inspect the run. The workspace identity SHALL be a runtime execution fact, not a project or repository configuration path.

#### Scenario: Workspace persisted for active run
- **WHEN** a WorkflowRun starts successfully
- **THEN** the run state SHALL retain enough workspace identity to resume tasks and serve review data for that run
- **AND** the persisted Project and Repository models SHALL remain free of local execution path fields

#### Scenario: Workspace cleanup does not mutate repository configuration
- **WHEN** a completed or abandoned workflow workspace is cleaned up
- **THEN** cleanup SHALL affect only runner-managed workspace state
- **AND** repository Git URL and base branch configuration SHALL remain unchanged
