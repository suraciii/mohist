## MODIFIED Requirements

### Requirement: Stage task policies can reference named agent sessions

Stage task execution policy SHALL allow an agent-session task to declare an optional `agentSessionRef` that names the logical agent session used by that task within the current stage attempt. The field SHALL be interpreted only by agent-session execution and SHALL NOT imply previous-task reuse or a session group.

#### Scenario: Agent-session policy carries a named ref
- **WHEN** a stage definition declares an agent-session task with `agentSessionRef: "plan-artifacts"`
- **THEN** dispatch SHALL pass that reference to the agent-session task input
- **AND** task identity, ordering, status, attempts, outputs, and artifact validation SHALL remain separate from the session reference

#### Scenario: Omitted ref keeps task-local behavior
- **WHEN** an agent-session task policy omits `agentSessionRef`
- **THEN** dispatch SHALL build the same task-local agent-session input used before this change
- **AND** Build and Check tasks SHALL remain task-local unless their policies explicitly set a reference

### Requirement: Default Plan artifact tasks share one planning session reference

The built-in Plan stage definition SHALL configure `proposal`, `specs`, `design`, `tasks`, and `self-review` agent-session tasks with the same `agentSessionRef`, `plan-artifacts`, while keeping repair and rebase operational tasks separate unless explicitly configured otherwise.

#### Scenario: Default Plan policies use plan-artifacts
- **WHEN** the built-in workflow definition is loaded
- **THEN** the default Plan artifact task policies for `proposal`, `specs`, `design`, `tasks`, and `self-review` SHALL declare `agentSessionRef: "plan-artifacts"`
- **AND** each artifact task SHALL still appear as an independent Plan task row

#### Scenario: Stage can define multiple named refs
- **WHEN** a stage definition assigns different agent-session tasks to two or more distinct `agentSessionRef` values
- **THEN** tasks with the same ref SHALL share one real session for the stage attempt
- **AND** tasks with different refs SHALL use different real sessions
