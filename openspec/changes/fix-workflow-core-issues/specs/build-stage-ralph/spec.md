## ADDED Requirements

### Requirement: Build Stage Delegates to RalphExecutor for OpenSpec Tasks

When the Build stage detects an OpenSpec change with prd.json tasks, it SHALL delegate execution to RalphExecutor instead of using spawn_coder directly.

#### Scenario: Build stage with prd.json tasks
- **WHEN** executeBuildStage is called and prd.json exists with one or more tasks
- **THEN** the system SHALL create a RalphExecutor instance and call execute() with the detected OpenSpecChange

#### Scenario: Build stage without prd.json
- **WHEN** executeBuildStage is called and no prd.json exists in the change directory
- **THEN** the system SHALL fall back to the existing spawn_coder behavior

#### Scenario: RalphExecutor result mapping
- **WHEN** RalphExecutor.execute() returns a RalphLoopResult
- **THEN** the system SHALL map it to a StageResult with success based on result.success, and requiresApproval when result.paused is true

### Requirement: onAskUser Connected to AgentRunnerService Pause

The RalphExecutor onAskUser callback SHALL trigger an AgentRunnerService pause and wait for user response via a Promise-based mechanism.

#### Scenario: Task failure triggers ask_user
- **WHEN** a task fails and RalphExecutor invokes onAskUser with a question
- **THEN** the system SHALL store the question with a resolve callback, emit an ask_user event, and the Promise SHALL remain pending until resume provides an answer

#### Scenario: Resume resolves pending question
- **WHEN** AgentRunnerService.resume() is called with a user message
- **THEN** the system SHALL resolve the pending onAskUser Promise with the user message, allowing RalphExecutor to continue
