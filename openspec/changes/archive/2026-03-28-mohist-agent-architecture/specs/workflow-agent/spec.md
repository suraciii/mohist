## ADDED Requirements

### Requirement: Per-issue Main Agent session
The system SHALL maintain one Main Agent session per active issue. M1: sessions are in-memory only, NOT persisted to SQLite. Server restart loses all session data — running issues must be re-started.

#### Scenario: Issue started
- **WHEN** a new issue is started
- **THEN** the system SHALL create a Main Agent session associated with the issue
- **THEN** the Main Agent SHALL begin the first stage (design)

#### Scenario: Issue completed
- **WHEN** an issue reaches the final stage (done)
- **THEN** the Main Agent session SHALL be closed

### Requirement: Workflow orchestration
The Main Agent SHALL orchestrate the workflow by: evaluating the current stage (hardcoded: design → implement → done), spawning opencode via spawn_agent for each stage, evaluating the subprocess output, and calling advance_stage to progress. M1: all gates are auto — no pause for approval.

#### Scenario: Stage advance
- **WHEN** the opencode subprocess completes successfully
- **THEN** the Main Agent SHALL call advance_stage to move to the next stage

#### Scenario: Sub-agent retry
- **WHEN** the opencode subprocess fails or its output does not meet expectations
- **THEN** the Main Agent MAY retry with a modified prompt

#### Scenario: Sub-agent failure
- **WHEN** the opencode subprocess fails (error or timeout)
- **THEN** the Main Agent SHALL analyze the failure and decide: retry or mark failed

### Requirement: Main Agent system prompt
The Main Agent SHALL have a system prompt that includes: its role as workflow orchestrator, available tools (spawn_agent, advance_stage, add_comment, get_issue), current issue context (title, description, stage), workflow stages (hardcoded: design → implement → done), and error handling guidelines.

#### Scenario: Dynamic prompt generation
- **WHEN** the Main Agent makes an LLM call
- **THEN** the system prompt SHALL include the current issue information
- **THEN** the prompt SHALL be dynamically generated per issue
