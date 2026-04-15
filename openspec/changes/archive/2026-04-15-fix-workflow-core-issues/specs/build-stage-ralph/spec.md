## ADDED Requirements

### Requirement: Build Stage Delegates to RalphExecutor for OpenSpec Tasks

When the Build stage detects an OpenSpec change with prd.json tasks, it SHALL delegate execution to RalphExecutor instead of using spawn_coder directly. The onAskUser callback SHALL NOT be provided — failed tasks are marked as failed and the loop continues.

#### Scenario: Build stage with prd.json tasks
- **WHEN** executeBuildStage is called and prd.json exists with one or more tasks
- **THEN** the system SHALL create a RalphExecutor instance WITHOUT onAskUser callback and call execute() with the detected OpenSpecChange

#### Scenario: Build stage without prd.json
- **WHEN** executeBuildStage is called and no prd.json exists in the change directory
- **THEN** the system SHALL fall back to the existing spawn_coder behavior

#### Scenario: RalphExecutor result mapping
- **WHEN** RalphExecutor.execute() returns a RalphLoopResult
- **THEN** the system SHALL map it to a StageResult with success based on result.success, and requiresApproval: true when result.failed > 0

#### Scenario: Task failure handled without user intervention
- **WHEN** a task fails after max retries in RalphExecutor
- **THEN** RalphExecutor SHALL mark the task as failed and continue to the next task (no onAskUser callback provided)
- **THEN** the final RalphLoopResult SHALL reflect all completed and failed tasks
