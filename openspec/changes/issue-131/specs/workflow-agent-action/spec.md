### Requirement: Agent definition reference action
Workflow profiles SHALL support `uses: mohist/agent` for task work. Its `with` input MUST contain `name` and `prompt`, MAY contain `session` and `timeout`, and MUST reject any input that is not part of that contract. `name` MUST resolve an Agent by the same name-or-id rules used by the Agent command surface; `prompt` MUST support workflow template expressions.

#### Scenario: A task references an Agent by name
- **WHEN** a workflow task declares `uses: mohist/agent` with an active Agent name and a prompt
- **THEN** the task is eligible for dispatch through that Agent definition

#### Scenario: A task omits a required Action input
- **WHEN** a workflow profile declares `mohist/agent` without `name` or `prompt`
- **THEN** profile validation MUST reject the task as invalid Action input

### Requirement: Definition-independent profile validation
Workflow profile save and workflow validation MUST validate the `mohist/agent` input shape without requiring the referenced Agent to exist or be active.

#### Scenario: A profile precedes its Agent definition
- **WHEN** a valid `mohist/agent` task references an Agent that does not yet exist
- **THEN** the profile save and validation MUST succeed without resolving the Agent

### Requirement: Dispatch-time Agent snapshot
Every `mohist/agent` task dispatch MUST resolve the referenced active Agent and bind that attempt to a snapshot of its instructions, selected runtime, model, and execution configuration. An already dispatched attempt MUST retain its snapshot when the Agent is subsequently edited; every retry MUST resolve a new snapshot from the current Agent definition.

#### Scenario: Editing an Agent after dispatch
- **WHEN** an Agent is edited after a task attempt using it has been dispatched
- **THEN** the active attempt MUST continue with its original Agent snapshot

#### Scenario: Retrying after an Agent edit
- **WHEN** a failed task is retried after its referenced Agent is edited
- **THEN** the retry MUST use the edited Agent definition

### Requirement: Agent execution input composition
`mohist/agent` MUST execute the Agent-selected OpenCode or Pi runtime using the resolved Agent instructions and execution configuration together with the task's rendered `prompt`. The task prompt MUST remain the current workflow work goal, and task input MUST NOT override the Agent-selected runtime or model configuration. `session` and `timeout` MUST retain the semantics of the selected runtime Action.

#### Scenario: A reusable reviewer receives a workflow-specific goal
- **WHEN** a task references an Agent with review instructions and supplies a workflow prompt
- **THEN** the runtime MUST receive both the Agent instructions and that task prompt, with the prompt defining the task's current goal

#### Scenario: The Agent selects the execution backend
- **WHEN** a task references an Agent configured for Pi
- **THEN** the task MUST execute through Pi rather than a runtime or model supplied by the task

### Requirement: Missing Agent dispatch failure
If the referenced Agent cannot be resolved at dispatch because it does not exist or is archived, the task attempt MUST fail with error code `agent_not_found`. Runtime failures after successful resolution MUST use the error behavior of the selected runtime Action so normal task recovery can evaluate them.

#### Scenario: A referenced Agent is archived before dispatch
- **WHEN** a pending task is dispatched after its referenced Agent has been archived
- **THEN** the task attempt MUST fail with `agent_not_found`

### Requirement: Workflow-owned execution lifecycle
A `mohist/agent` execution MUST remain Workflow work: its TaskRun SHALL own task status, result, outputs, retry, recovery, checks, and WorkflowRun advancement. The execution MUST use a Workflow-origin AgentSession addressed by its workflow session name or work ID default, and MUST NOT create an AgentJob, direct AgentSession, or separate Agent task lifecycle.

#### Scenario: An Agent-referenced task completes
- **WHEN** a `mohist/agent` task reports completion
- **THEN** its result MUST be reported to and decided by the owning WorkflowRun without creating an AgentJob

#### Scenario: A named workflow session is requested
- **WHEN** a `mohist/agent` task supplies `with.session`
- **THEN** the runtime execution MUST use that name within the owning WorkflowRun's AgentSession namespace
