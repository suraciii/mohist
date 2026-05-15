## ADDED Requirements

### Requirement: REQ-WR-RECOVERY-001 Retry failed work in current stage

WorkflowRun SHALL make retry target the failed work in the latest failed current-stage attempt. Retry SHALL preserve earlier successful same-stage work that remains valid, reset the failed work and downstream dependent work, clear current failure/approval state, and keep the WorkflowRun in the same stage.

#### Scenario: Retry failed task preserves earlier completed tasks
- **WHEN** the latest WorkflowRun is failed in the current stage because a task failed
- **AND** earlier tasks in the same stage are completed and still valid
- **THEN** retry reopens the run and current StageRun
- **AND** the failed task is reset to pending
- **AND** downstream same-stage tasks and checks are reset as needed
- **AND** earlier completed tasks remain completed
- **AND** the current stage is unchanged

#### Scenario: Retry failed check preserves completed tasks
- **WHEN** the latest WorkflowRun is failed in the current stage because a check failed
- **AND** all required tasks in that stage completed successfully
- **THEN** retry reopens the run and current StageRun
- **AND** completed tasks remain completed
- **AND** the failed check and downstream checks are reset
- **AND** work derived from that failed check is invalidated where applicable
- **AND** the current stage is unchanged

### Requirement: REQ-WR-RECOVERY-002 Rerun current stage from first work

WorkflowRun SHALL make rerun discard the current stage attempt state and restart the same stage from its first work item. Rerun SHALL clear current-stage task/check progress, failure, approval, and retry-derived state while preserving earlier passed stages and leaving currentStage unchanged.

#### Scenario: Rerun resets current stage from beginning
- **WHEN** rerun is requested for a non-backlog, non-done current stage
- **THEN** the WorkflowRun remains in the same current stage
- **AND** all current-stage tasks and checks are reset for execution from the first work item
- **AND** current-stage failure and approval state are cleared
- **AND** earlier passed stages remain passed

#### Scenario: Plan rerun makes first Plan work next
- **WHEN** rerun is requested while the current stage is Plan
- **AND** Plan artifacts from the prior attempt already exist
- **THEN** WorkflowRun reports the first Plan work as pending next work
- **AND** existing artifact files alone do not mark that work complete
