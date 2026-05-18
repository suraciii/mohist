# OpenSpec Capability: workflow-agent

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

### Requirement: REQ-WA-001 Workflow consumes session results without judging liveness

Workflow orchestration SHALL consume completed, failed, or cancelled session call results from tasks and SHALL NOT independently determine whether opencode is alive.

#### Scenario: Workflow receives session failure
- **WHEN** a task reports that its opencode session failed
- **THEN** workflow SHALL handle that as a task/session execution result
- **AND** workflow SHALL decide retry, block, interruption, or user action through existing workflow policy

#### Scenario: Session state does not mutate issue state directly
- **WHEN** a session enters `probing` or `failed`
- **THEN** issue `stage` and `status` SHALL remain unchanged unless a separate workflow decision changes them later

### Requirement: Agent-session tasks resolve named refs through stage attempt context

Agent-session task execution SHALL resolve an optional `agentSessionRef` through a stage-attempt-scoped registry. The registry SHALL return one real AgentSession for repeated uses of the same ref in the same attempt and SHALL leave omitted refs on the existing task-local create, execute, and close lifecycle.

#### Scenario: Named ref reuses one AgentSession
- **WHEN** two agent-session tasks in the same stage attempt execute with the same `agentSessionRef`
- **THEN** the task handler SHALL execute both prompts against the same real AgentSession
- **AND** the handler SHALL NOT close that named session after the first task completes

#### Scenario: Omitted ref remains task-local
- **WHEN** an agent-session task executes without `agentSessionRef`
- **THEN** the task handler SHALL create a task-local AgentSession
- **AND** it SHALL close that task-local session when the task execution finishes

#### Scenario: Stage lifecycle closes named sessions
- **WHEN** the owning stage attempt reaches a terminal boundary such as passed, failed, awaiting approval, cancelled, or pipeline completion
- **THEN** the workflow runtime SHALL close and remove all named sessions for that attempt
- **AND** close behavior SHALL be idempotent and best-effort on error paths

