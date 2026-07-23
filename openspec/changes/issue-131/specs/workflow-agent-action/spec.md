### Requirement: Agent definition reference action
Workflow profiles SHALL support `uses: mohist/agent` for task and check work. Its `with` input MUST contain `name` and `prompt`, MAY contain `session` and `timeout`, and MUST reject any input that is not part of that contract. `name` MUST resolve an Agent by the same name-or-id rules used by the Agent command surface; `prompt` MUST support workflow template expressions.

#### Scenario: A task references an Agent by name
- **WHEN** a workflow task declares `uses: mohist/agent` with an active Agent name and a prompt
- **THEN** the task is eligible for dispatch through that Agent definition

#### Scenario: A task omits a required Action input
- **WHEN** a workflow profile declares `mohist/agent` without `name` or `prompt`
- **THEN** profile validation MUST reject the task as invalid Action input

#### Scenario: A check references an Agent
- **WHEN** a workflow check declares `uses: mohist/agent` with an active Agent name and a prompt
- **THEN** the check is eligible for dispatch through that Agent definition

### Requirement: Definition-independent profile validation
Workflow profile save and workflow validation MUST validate the `mohist/agent` input shape without requiring the referenced Agent to exist or be active.

#### Scenario: A profile precedes its Agent definition
- **WHEN** a valid `mohist/agent` task references an Agent that does not yet exist
- **THEN** the profile save and validation MUST succeed without resolving the Agent

### Requirement: Dispatch-time Agent snapshot
Every `mohist/agent` task or check dispatch MUST resolve the referenced active Agent and persist the concrete transformed `WorkDispatch` on the owning active WorkflowRun work before it is offered to a Runner. The snapshot MUST contain the raw workflow inputs, rendered-context variables, selected runtime, instructions, model, variant, and timeout input. An already dispatched attempt MUST reoffer that stored envelope verbatim after Agent edits, server restart, or grain activation; every task retry or new checks attempt MUST resolve a new snapshot from the current Agent definition.

#### Scenario: Editing an Agent after dispatch
- **WHEN** an Agent is edited after a task attempt using it has been dispatched
- **THEN** the active attempt MUST continue with its original Agent snapshot

#### Scenario: Retrying after an Agent edit
- **WHEN** a failed task is retried after its referenced Agent is edited
- **THEN** the retry MUST use the edited Agent definition

#### Scenario: Reoffering a check after an Agent edit
- **WHEN** a checks attempt using an Agent has been offered, the Agent is edited, and the Runner has not acknowledged the work
- **THEN** the reoffer MUST carry the original transformed check envelope rather than resolving the edited Agent

### Requirement: Agent execution input composition
`mohist/agent` MUST execute the Agent-selected OpenCode or Pi runtime using the resolved Agent instructions and execution configuration together with the task or check's rendered `prompt`. The transformed Action input MUST be `{ prompt, session?, timeout?, options: { instructions, model?, variant? } }`; `options` is closed to those keys. `instructions` precedes the rendered prompt, and the rendered prompt remains the current workflow work goal. The task or check input MUST NOT override the Agent-selected runtime or model configuration. `session` and `timeout` MUST retain the semantics of the selected runtime Action: `timeout` is the per-turn deadline in milliseconds and defaults to that Action's existing one-hour default when absent.

#### Scenario: A reusable reviewer receives a workflow-specific goal
- **WHEN** a task references an Agent with review instructions and supplies a workflow prompt
- **THEN** the runtime MUST receive both the Agent instructions and that task prompt, with the prompt defining the task's current goal

#### Scenario: The Agent selects the execution backend
- **WHEN** a task references an Agent configured for Pi
- **THEN** the task MUST execute through Pi rather than a runtime or model supplied by the task

#### Scenario: An Agent check times out
- **WHEN** an Agent-referenced check supplies `timeout` and its selected runtime reaches that deadline
- **THEN** the check result MUST carry the selected runtime's existing timeout failure code and message

### Requirement: Missing Agent dispatch failure
If the referenced Agent cannot be resolved at dispatch because it does not exist or is archived, the task attempt MUST fail with `ExecutionError.code = agent_not_found`; a check attempt MUST report a named failed `CheckResult` for that Agent-referenced check with `ExecutionError.code = agent_not_found`. Other checks blocked in that same checks envelope MUST report `check-not-run`, and no Runner claim or AgentSession is created. Runtime failures after successful resolution MUST use the error behavior of the selected runtime Action so normal task recovery can evaluate them.

#### Scenario: A referenced Agent is archived before dispatch
- **WHEN** a pending task is dispatched after its referenced Agent has been archived
- **THEN** the task attempt MUST fail with `agent_not_found`

#### Scenario: A referenced Agent check is archived before dispatch
- **WHEN** a pending check is dispatched after its referenced Agent has been archived
- **THEN** that check MUST report `agent_not_found`, the remaining checks in its envelope MUST report `check-not-run`, and WorkflowRun MUST make its normal check/recovery decision from those reports

### Requirement: Workflow-owned execution lifecycle
A `mohist/agent` execution MUST remain Workflow work: its TaskRun or named CheckResult SHALL own status, result, outputs, retry or recovery, and WorkflowRun advancement. The execution MUST use a Workflow-origin AgentSession addressed by its workflow session name or work ID default, and MUST NOT create an AgentJob, direct AgentSession, or separate Agent task lifecycle.

#### Scenario: An Agent-referenced task completes
- **WHEN** a `mohist/agent` task reports completion
- **THEN** its result MUST be reported to and decided by the owning WorkflowRun without creating an AgentJob

#### Scenario: A named workflow session is requested
- **WHEN** a `mohist/agent` task supplies `with.session`
- **THEN** the runtime execution MUST use that name within the owning WorkflowRun's AgentSession namespace
