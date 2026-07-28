### Requirement: Agent definition owns execution selection
For every new direct AgentJob and `mohist/agent` workflow task attempt, the resolved active Agent definition SHALL be the sole source of its Instructions, Runtime, Model, Variant, and Skills. The task prompt and contextual references remain caller input, but SHALL NOT replace or modify any execution-definition field. An absent Runtime SHALL resolve to the Agent configuration default.

#### Scenario: Direct launch uses the Agent Runtime
- **WHEN** an active Agent configured for `pi` is launched with a valid prompt and context
- **THEN** the created AgentSession and AgentJob dispatch SHALL use `pi`, with the Agent's configured model, variant, instructions, and Skills

#### Scenario: Workflow task uses the Agent execution definition
- **WHEN** a `mohist/agent` task references an active Agent configured for `opencode`
- **THEN** its task attempt SHALL dispatch through `mohist/opencode` with that Agent's instructions, model, variant, and Skills while retaining the workflow task prompt as the work goal

### Requirement: Entry points cannot override execution definition
The direct Agent launch contract SHALL NOT accept a Runtime override. A request that supplies a Runtime override SHALL be rejected before an AgentSession or AgentJob is created. Issue-scoped variables, routing context, and event-routing rules SHALL NOT override the Runtime or any other execution-definition field of a named Agent.

#### Scenario: Launch request attempts a Runtime override
- **WHEN** a client submits a direct Agent launch request containing a `runtime` value
- **THEN** the system SHALL reject the request as invalid and SHALL create neither an AgentSession nor an AgentJob

#### Scenario: Issue context declares another Runtime
- **WHEN** a direct or routed Agent launch has Issue context whose variables declare a Runtime different from the active Agent's Runtime
- **THEN** the launch SHALL use the Agent's Runtime and SHALL not read the Issue Runtime as an override

### Requirement: Direct AgentJobs retain a durable execution snapshot
Before a direct AgentJob is offered to a Runner, the system SHALL durably capture the resolved Agent identity, Instructions, Runtime, Model, Variant, and ordered Skills together with the prompt. Redelivery, recovery, and reoffer of that AgentJob SHALL use this captured snapshot and SHALL NOT reread the live Agent definition. Subsequent inputs to the created AgentSession SHALL retain the Session's established execution definition.

#### Scenario: Agent is edited after launch
- **WHEN** an AgentJob has been accepted and its Agent's instructions, Runtime, model, variant, or Skills are edited before the job executes or is reoffered
- **THEN** the accepted AgentJob and its AgentSession SHALL continue with the originally captured execution definition

#### Scenario: A later launch sees an edited definition
- **WHEN** an Agent definition is edited and a new direct launch is submitted afterwards
- **THEN** the new AgentJob SHALL capture the edited execution definition

### Requirement: Workflow Agent attempts snapshot the resolved definition
For a `mohist/agent` task, the system SHALL resolve the referenced active Agent while creating the task attempt dispatch snapshot and persist the concrete transformed dispatch before offering it to a Runner. The persisted attempt SHALL be reoffered verbatim after restart or Agent edits. A retry SHALL resolve the Agent definition again and create a new snapshot. The WorkflowRun SHALL remain the authority for task state and recovery; this Action SHALL NOT create an AgentJob or a direct AgentSession.

#### Scenario: Reoffer preserves the original workflow attempt
- **WHEN** a `mohist/agent` task has a persisted dispatch snapshot and its Agent is edited before a Runner reoffer
- **THEN** the reoffer SHALL use the persisted instructions, Runtime, model, variant, and Skills from that attempt

#### Scenario: Retry adopts the current Agent definition
- **WHEN** a failed `mohist/agent` task is retried after its Agent definition is edited
- **THEN** the retry SHALL create a new dispatch snapshot using the edited execution definition

### Requirement: Skills are delivered as execution input
The ordered Skills captured from an Agent definition SHALL be delivered to the selected Runtime for both direct AgentJobs and `mohist/agent` workflow task attempts. The Runtime SHALL receive no Skills that are not in the captured definition, and an Agent with no Skills SHALL produce an execution request with no configured Skills.

#### Scenario: Configured Skills are available to a direct AgentJob
- **WHEN** a direct AgentJob is launched from an Agent whose Skills are `mohist` and `mohist-explore`
- **THEN** its selected Runtime SHALL receive those two Skills in that order as part of the captured execution input

#### Scenario: Workflow Agent task has no configured Skills
- **WHEN** a `mohist/agent` task references an Agent with an empty Skills list
- **THEN** the selected Runtime SHALL receive no configured Skills for that task attempt

### Requirement: Workflow Agent references fail only at dispatch
Workflow profile save and validation SHALL validate the shape of a `mohist/agent` task without requiring its referenced Agent to exist. At dispatch, a missing or archived Agent SHALL fail the task attempt with the structured error code `agent_not_found` before Runner execution.

#### Scenario: Profile precedes Agent creation
- **WHEN** a workflow profile containing a syntactically valid `mohist/agent` task references an Agent that does not yet exist
- **THEN** the profile SHALL save and validate successfully

#### Scenario: Referenced Agent is unavailable at dispatch
- **WHEN** a `mohist/agent` task is dispatched after its referenced Agent is missing or archived
- **THEN** the system SHALL record a failed task attempt with error code `agent_not_found` and SHALL not offer work to a Runner
