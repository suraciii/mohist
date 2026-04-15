## MODIFIED Requirements

### Requirement: RalphExecutor Runs Without onAskUser in Build Stage

When RalphExecutor is invoked from WorkflowController.executeBuildStage, the onAskUser callback SHALL NOT be provided. Failed tasks are recorded in the RalphLoopResult and surfaced to the user via the stage-level approval flow instead.

#### Scenario: no onAskUser callback provided
- **WHEN** executeBuildStage creates a RalphExecutor instance
- **THEN** the RalphExecutorContext SHALL NOT include an onAskUser callback

#### Scenario: failed tasks surfaced via stage approval
- **WHEN** RalphExecutor returns with failed > 0
- **THEN** executeBuildStage SHALL return a StageResult with requiresApproval: true and the failed task details in the output
