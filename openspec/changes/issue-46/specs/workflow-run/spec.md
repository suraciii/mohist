## MODIFIED Requirements

### Requirement: REQ-WR-001 Starting an issue creates a WorkflowRun

Starting an issue SHALL create or reuse one active WorkflowRun aggregate bound to the issue id and issue number only after the Issue is start eligible. The aggregate SHALL derive its first stage from its ordered stage definition, create ordered StageRuns for `plan`, `build`, `check`, and `integrate`, seed static Plan and Integrate task/check state, and start the first StageRun without using `Issue.stage` as the state-machine decision source. Workflow startup SHALL resolve repository context from the issue's current project repository reference and SHALL pass resolved repository identity and current repository facts into runtime variables instead of reading persisted issue repository snapshot data.

#### Scenario: Start creates aggregate-rooted run

- **WHEN** an issue is started
- **AND** the issue is start eligible
- **THEN** the system SHALL create or reuse one active WorkflowRun for that issue
- **AND** the WorkflowRun SHALL have `status = running` and `currentStage` equal to the first configured runnable stage
- **AND** the first StageRun SHALL be running
- **AND** issue stage/status updates SHALL be projections of the WorkflowRun decision

#### Scenario: Start is idempotent for active run

- **WHEN** start or resume code encounters an issue that already has a non-terminal active WorkflowRun
- **THEN** it SHALL reuse that WorkflowRun
- **AND** it SHALL NOT create a duplicate active run for the same issue

#### Scenario: Waiting prerequisite prevents WorkflowRun creation

- **WHEN** start-pipeline execution evaluates Issue #201
- **AND** Issue #201 is waiting for prerequisite Issue #200 to be delivered
- **THEN** the system SHALL NOT create or start a WorkflowRun for Issue #201
- **AND** the system SHALL NOT create an agent session for Issue #201
- **AND** the waiting condition SHALL be recorded as start eligibility state rather than workflow failure

#### Scenario: Workflow start resolves repository variables from current project config
- **WHEN** a workflow starts for an issue with a valid repository reference
- **THEN** workflow variables SHALL include the resolved repository identity and current repository facts such as repository id or name, path, remote, and base branch
- **AND** those values SHALL come from the current project repository configuration

#### Scenario: Workflow start fails clearly for missing repository reference
- **WHEN** a workflow starts for an issue whose repository reference cannot be resolved in the current project configuration
- **THEN** the workflow SHALL fail or block with a repository configuration problem
- **AND** it SHALL NOT silently fall back to stale issue repository data or an implicit `main` branch
