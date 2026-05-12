## MODIFIED Requirements

### Requirement: REQ-WFE-WORKFLOW-RUN-001 Workflow engine updates WorkflowRun runtime state

The workflow engine SHALL update the active WorkflowRun as stages execute. Stage lifecycle, task results, check results, and approval snapshots SHALL be written to WorkflowRun while existing `stage_executions` and workflow logs remain audit evidence.

#### Scenario: Stage lifecycle updates StageRun

- **WHEN** a runner starts, passes, fails, or awaits approval for a stage
- **THEN** the matching WorkflowRun StageRun SHALL reflect the current status
- **AND** the WorkflowRun `currentStage` and status SHALL remain consistent with issue progression

#### Scenario: Task and check results update WorkflowRun

- **WHEN** a runner records a task result or check result
- **THEN** the matching WorkflowRun task or check SHALL be updated
- **AND** the same result MAY continue to be recorded in `stage_executions` for audit

#### Scenario: Approval snapshot is stored on StageRun

- **WHEN** Plan or Check requests user approval
- **THEN** the matching StageRun SHALL store an approval snapshot
- **AND** approval SHALL NOT be promoted to a first-class policy or decision model in this change
