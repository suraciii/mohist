## MODIFIED Requirements

### Requirement: REQ-WR-001 Starting an issue creates a WorkflowRun

Starting an issue SHALL create or reuse one active WorkflowRun aggregate bound to the issue id and issue number only after the Issue is start eligible. The aggregate SHALL derive its first stage from its ordered stage definition, create ordered StageRuns for `plan`, `build`, `check`, and `integrate`, seed static Plan and Integrate task/check state, and start the first StageRun without using `Issue.stage` as the state-machine decision source. Workflow start SHALL materialize and bind the run's single execution workspace before the first task is dispatched, and work-item dispatch SHALL consume that bound workspace rather than create or re-create it.

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

#### Scenario: Start materializes and binds the workspace before the first task dispatch

- **WHEN** an issue is started and is start eligible
- **THEN** workflow start SHALL materialize the run's single execution workspace before the first StageRun task is scheduled
- **AND** the WorkflowRun SHALL bind the workspace identity (path, run branch, owning run id) before the first task is dispatched
- **AND** the first task SHALL NOT be dispatched until the workspace is materialized and bound

#### Scenario: Work-item dispatch consumes the bound workspace

- **WHEN** a task or check is dispatched within a WorkflowRun that has bound its workspace
- **THEN** dispatch SHALL execute against the already-bound workspace
- **AND** dispatch SHALL NOT create or re-materialize the workflow workspace as a side effect of preparing the work item
