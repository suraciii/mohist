## MODIFIED Requirements

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
