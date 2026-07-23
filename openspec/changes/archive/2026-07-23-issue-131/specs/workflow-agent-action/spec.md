### Requirement: Agent definition reference action
Workflow profiles SHALL support `uses: mohist/agent` for task work. Its `with` input MUST contain `name` and `prompt`, MAY contain `session` and `timeout`, and MUST reject any input that is not part of that contract. `name` MUST be a static string: a value containing a workflow template expression MUST fail profile validation. `prompt` MUST support workflow template expressions. `name` MUST resolve an Agent by the same name-or-id rules used by the Agent command surface: an `agent_*` reference is an id lookup only; every other reference is looked up by name first, then by id only when no matching name exists.

Check support is out of scope: `mohist/agent` is not a valid `uses` value for workflow checks. Extending the contract to checks requires a separate contract-change issue.

#### Scenario: A task references an Agent by name
- **WHEN** a workflow task declares `uses: mohist/agent` with an active Agent name and a prompt
- **THEN** the task is eligible for dispatch through that Agent definition

#### Scenario: A task omits a required Action input
- **WHEN** a workflow profile declares `mohist/agent` without `name` or `prompt`
- **THEN** profile validation MUST reject the task as invalid Action input

#### Scenario: A check references an Agent
- **WHEN** a workflow check declares `uses: mohist/agent`
- **THEN** profile validation MUST reject the check as an unsupported Action for check work

#### Scenario: A reference name contains a template expression
- **WHEN** a workflow task declares `mohist/agent` with `name: ${{ variables.agent }}`
- **THEN** profile validation MUST reject the Action input before dispatch

#### Scenario: A name collides with a legacy non-prefixed id
- **WHEN** a reference that does not start with `agent_` matches one Agent name and another Agent's id
- **THEN** dispatch MUST select the Agent matched by name

### Requirement: Definition-independent profile validation
Workflow profile save and workflow validation MUST validate the `mohist/agent` input shape without requiring the referenced Agent to exist or be active.

#### Scenario: A profile precedes its Agent definition
- **WHEN** a valid `mohist/agent` task references an Agent that does not yet exist
- **THEN** the profile save and validation MUST succeed without resolving the Agent

### Requirement: Dispatch-time Agent snapshot
Every `mohist/agent` task dispatch MUST resolve the referenced active Agent and persist the concrete transformed `WorkDispatch` on the owning active WorkflowRun work before it is offered to a Runner. The snapshot MUST contain the raw workflow inputs, rendered-context variables, selected runtime, the composed prompt, model, variant, and timeout input. An already dispatched attempt MUST reoffer that stored envelope verbatim after Agent edits, server restart, or grain activation; every task retry MUST resolve a new snapshot from the current Agent definition.

#### Scenario: Editing an Agent after dispatch
- **WHEN** an Agent is edited after a task attempt using it has been dispatched
- **THEN** the active attempt MUST continue with its original Agent snapshot

#### Scenario: Retrying after an Agent edit
- **WHEN** a failed task is retried after its referenced Agent is edited
- **THEN** the retry MUST use the edited Agent definition

### Requirement: Agent execution input composition
`mohist/agent` MUST execute the Agent-selected OpenCode or Pi runtime using the resolved Agent instructions together with the task's `prompt`. The server MUST compose the resolved Agent instructions and the task's raw prompt into a single composed prompt at dispatch time: instructions MUST precede the raw prompt, and the raw prompt remains the current workflow work goal. Template expressions in the raw prompt MUST still be rendered by the Runner against the immutable attempt context; the server composes against the unrendered raw prompt, so template rendering continues to occur at the execution boundary exactly as for inline Actions.

The transformed Action input delivered to the Runner MUST be `{ prompt (composed), session?, timeout?, options: { model?, variant? } }`. This is the existing published input contract of `mohist/opencode` and `mohist/pi`; the resolved Agent instructions MUST NOT be carried as a new Runner Action `options` key. The task input MUST NOT override the Agent-selected runtime or model configuration beyond what those runtime Actions already accept. `session` and `timeout` MUST retain the semantics of the selected runtime Action: `timeout` is the per-turn deadline in milliseconds and defaults to that Action's existing one-hour default when absent. The existing inline behavior of both runtime Actions, including their handling of unknown `options` keys, MUST remain unchanged.

#### Scenario: A reusable reviewer receives a workflow-specific goal
- **WHEN** a task references an Agent with review instructions and supplies a workflow prompt
- **THEN** the runtime MUST receive a single prompt whose body is the Agent instructions followed by the task prompt, with the task prompt defining the task's current goal

#### Scenario: The Agent selects the execution backend
- **WHEN** a task references an Agent configured for Pi
- **THEN** the task MUST execute through Pi rather than a runtime or model supplied by the task

#### Scenario: Existing inline Action behavior is preserved
- **WHEN** a workflow uses inline `mohist/opencode` or `mohist/pi` without `mohist/agent`
- **THEN** the runtime Actions MUST accept, reject, or diagnose their `options` exactly as they do today

### Requirement: Missing Agent dispatch failure
If the referenced Agent cannot be resolved at dispatch because it does not exist or is archived, the task attempt MUST fail with `ExecutionError.code = agent_not_found`; no Runner claim or AgentSession is created. Runtime failures after successful resolution MUST use the error behavior of the selected runtime Action so normal task recovery can evaluate them.

#### Scenario: A referenced Agent is archived before dispatch
- **WHEN** a pending task is dispatched after its referenced Agent has been archived
- **THEN** the task attempt MUST fail with `agent_not_found`

### Requirement: Workflow-owned execution lifecycle
A `mohist/agent` execution MUST remain Workflow work: its TaskRun SHALL own status, result, outputs, retry or recovery, and WorkflowRun advancement. The execution MUST use a Workflow-origin AgentSession addressed by its workflow session name or work ID default, and MUST NOT create an AgentJob, direct AgentSession, or separate Agent task lifecycle.

#### Scenario: An Agent-referenced task completes
- **WHEN** a `mohist/agent` task reports completion
- **THEN** its result MUST be reported to and decided by the owning WorkflowRun without creating an AgentJob

#### Scenario: A named workflow session is requested
- **WHEN** a `mohist/agent` task supplies `with.session`
- **THEN** the runtime execution MUST use that name within the owning WorkflowRun's AgentSession namespace
