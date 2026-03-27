## ADDED Requirements

### Requirement: Per-issue persistent session
The system SHALL maintain one Workflow Agent (Main Agent) session per active issue. The session SHALL persist across gate pauses and server restarts. The session SHALL contain the full conversation history between the Main Agent LLM and its tools.

#### Scenario: Issue created
- **WHEN** a new issue is created and started
- **THEN** the system SHALL create a Main Agent session associated with the issue
- **THEN** the Main Agent SHALL read workflow.yaml and begin the first stage

#### Scenario: Server restart
- **WHEN** Mohist server restarts with active issues
- **THEN** the system SHALL restore Main Agent sessions from SQLite
- **THEN** the Main Agent SHALL resume from the last known state

#### Scenario: Issue completed
- **WHEN** an issue reaches the final stage (done)
- **THEN** the Main Agent session SHALL be closed and marked as completed

### Requirement: Workflow orchestration
The Main Agent SHALL read workflow.yaml and orchestrate the workflow by: evaluating the current stage, spawning the appropriate sub-agent, evaluating the sub-agent's output against the stage's `expects`, and deciding whether to advance, retry, or wait.

#### Scenario: Stage advance
- **WHEN** a sub-agent completes and its output satisfies the stage's `expects`
- **AND** the stage's `gate_after` is `auto`
- **THEN** the Main Agent SHALL advance to the next stage

#### Scenario: Gate approval
- **WHEN** a sub-agent completes and its output satisfies the stage's `expects`
- **AND** the stage's `gate_after` is `approve`
- **THEN** the Main Agent SHALL pause and wait for user approval

#### Scenario: Sub-agent retry
- **WHEN** a sub-agent completes but its output does not satisfy the stage's `expects`
- **THEN** the Main Agent MAY retry with a modified prompt or context

#### Scenario: Sub-agent failure
- **WHEN** a sub-agent fails (error or timeout)
- **THEN** the Main Agent SHALL analyze the failure and decide: retry, ask user, or mark blocked

### Requirement: Gate management
The Main Agent SHALL handle gate_after semantics: `approve` gates SHALL pause the Main Agent session until the user explicitly approves. `auto` gates SHALL advance immediately after the sub-agent completes.

#### Scenario: Auto gate
- **WHEN** a stage with `gate_after: auto` completes
- **THEN** the Main Agent SHALL immediately advance to the next stage

#### Scenario: Approve gate
- **WHEN** a stage with `gate_after: approve` completes
- **THEN** the Main Agent SHALL enter a paused state
- **WHEN** the user sends an approve command
- **THEN** the Main Agent SHALL resume and advance to the next stage

### Requirement: Rollback handling
The Main Agent SHALL support user-initiated rollback. When the user requests rollback to a previous stage, the Main Agent SHALL cancel the current sub-agent, update the issue stage, and spawn a new sub-agent for the target stage.

#### Scenario: Rollback to previous stage
- **WHEN** the user requests rollback to a previous stage
- **THEN** the Main Agent SHALL cancel the current sub-agent (if running)
- **THEN** the issue stage SHALL be updated to the target stage
- **THEN** a rollback event SHALL be appended to the workflow log
- **THEN** the Main Agent SHALL spawn a new sub-agent for the target stage

### Requirement: Main Agent system prompt
The Main Agent SHALL have a system prompt that includes: its role as workflow orchestrator, available tools, current issue context (title, description, stage), workflow definition (stages from workflow.yaml), gate semantics, and error handling guidelines.

#### Scenario: Dynamic prompt injection
- **WHEN** the Main Agent makes an LLM call
- **THEN** the system prompt SHALL include the current issue information and workflow definition
- **THEN** the prompt SHALL be dynamically generated, not hardcoded
