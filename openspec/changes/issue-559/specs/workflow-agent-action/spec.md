### Requirement: Workflow Agent task contract
`uses: mohist/agent` SHALL be valid for Workflow task work and SHALL remain invalid for Workflow checks. Its `with` object MUST contain `name` and `prompt` string inputs, MUST contain no keys other than `name`, `prompt`, `session`, and `timeout`, and MUST treat `session` and `timeout` as optional. `name` MUST be a literal Agent reference; `prompt` MUST support the normal Workflow template context, and `session` and `timeout` MUST retain their existing template and runtime semantics. Profile validation MUST validate this input shape without requiring the referenced Agent to exist or be active.

#### Scenario: A valid Workflow task references an Agent
- **WHEN** a Workflow task uses `mohist/agent` with a literal Agent reference and a prompt
- **THEN** profile validation MUST accept the task and preserve the prompt for the Workflow attempt context

#### Scenario: A Workflow check references an Agent
- **WHEN** a Workflow check uses `mohist/agent`, regardless of whether its inputs are otherwise valid
- **THEN** profile validation MUST reject the check as an unsupported use of the Action

#### Scenario: Profile validation precedes Agent creation
- **WHEN** a valid `mohist/agent` task references an Agent that does not yet exist
- **THEN** profile save and Workflow validation MUST succeed without resolving live Agent state

#### Scenario: Invalid Action input is rejected
- **WHEN** a `mohist/agent` task omits `name` or `prompt`, uses a non-string value, uses a templated `name`, or supplies an unknown input
- **THEN** validation MUST reject the task with an actionable input error before execution

### Requirement: Agent handoff transport and acknowledgement
After a `mohist/agent` task is claimed, the Runner SHALL treat an `agent-handoff` dispatch as temporary non-execution transport. It MAY occupy a transient Runner claim, but it MUST NOT resolve a runtime, open an AgentSession, submit an Agent prompt, or control a process. The Runner SHALL render and validate the deferred input, then submit exactly this internal command: `POST /api/runner/{runnerId}/agent-handoffs` with `commandId` equal to the stable dispatch `workId`, the `requestFingerprint`, `projectId`, `workflowRunId`, `taskRunId`, `taskAttempt`, `workId`, and the rendered `{name, prompt, session?, timeout?}` input. The response SHALL be a JSON acknowledgement with `disposition` equal to `accepted`, `rejected`, or `retry`. `accepted` MUST include Job, Session, Input, and Turn identifiers; `rejected` MUST include a stable code and reason; `retry` MUST be nonterminal and MUST NOT create a second command identity.

The Server SHALL key the handoff command by `(projectId, commandId)` and persist the request fingerprint before promoting any Agent participant. Repeating the same command and fingerprint MUST replay the original acknowledgement; a conflicting fingerprint MUST return definitive `rejected` with `handoff_conflict`. A lost response, timeout, 5xx response, or `retry` acknowledgement MUST retain the same transport work and resend the same command and fingerprint. `accepted` and `rejected` MUST retire the transport obligation. Handoff acknowledgements MUST bypass `/report`, `ReceiveTaskReportAsync`, and `ReceiveCheckReportAsync`.

#### Scenario: Handoff transport is claimed before Agent preflight
- **WHEN** the Runner claims an `agent-handoff` dispatch for a task whose Agent is unavailable
- **THEN** the transport claim MAY exist temporarily, but no accepted AgentJob, AgentSession, SessionInput, AgentTurn, external Runtime execution, or Agent concurrency admission MUST be created

#### Scenario: A handoff is accepted
- **WHEN** the handoff command passes Agent selection, readiness, workspace, and Workflow acceptance
- **THEN** the Server MUST return `accepted` with the original command and fingerprint plus the stable Job, Session, Input, and Turn identifiers, and the Runner MUST retire only the transport work

#### Scenario: A handoff response is lost
- **WHEN** the Runner does not receive the response after the Server accepted or rejected the command
- **THEN** the Runner MUST resend the same command identity, fingerprint, and rendered input, and the Server MUST replay the prior acknowledgement without creating another lineage or entering the ordinary Workflow report path

### Requirement: Agent selection and acceptance
At execution time, the Action SHALL resolve the Agent within the Workflow Project using the canonical reference rule: a reference beginning with `agent_` MUST resolve by id only; every other reference MUST resolve by name first and by id only when no name matches. The resolved Agent MUST be active and retain a stable Agent identity for the invocation. A missing, archived, or otherwise unresolvable Agent MUST produce the structured `agent_not_found` failure and MUST NOT create accepted Agent execution or external Runner work.

#### Scenario: An Agent is selected by name
- **WHEN** a task references an active Agent by name
- **THEN** the invocation MUST use that Agent's stable id and MUST create no fallback runtime execution for another Agent

#### Scenario: An id-like reference is selected by id
- **WHEN** a task references `agent_<id>` and an Agent with that id exists
- **THEN** the invocation MUST select that id without treating the value as a display name

#### Scenario: A referenced Agent is unavailable at dispatch
- **WHEN** a pending task is dispatched after its referenced Agent is archived, deleted, or cannot be resolved
- **THEN** the task MUST fail with `agent_not_found`, and no accepted AgentJob, AgentSession, SessionInput, AgentTurn, external Runtime execution, or Agent concurrency admission MUST be created; any transient handoff transport claim MUST be retired as a rejected acknowledgement

### Requirement: Agent execution composition
An accepted invocation MUST execute the selected Agent definition together with the Workflow task prompt. The Agent's instructions MUST remain part of the execution input and the Workflow prompt MUST remain the invocation's task goal. The invocation MUST use the snapshotted Agent runtime, model, variant, and ordered Skills; task input MUST NOT replace those Agent-selected execution properties. The Action MUST use the existing runtime execution boundary and MUST NOT expose Runtime handles or Runner process controls to Workflow content.

#### Scenario: A reusable Agent receives a Workflow-specific goal
- **WHEN** an active Agent has instructions and a Workflow task supplies a prompt
- **THEN** the Agent execution MUST receive both the Agent instructions and the task prompt, with the task prompt preserved as the current work goal

#### Scenario: The Agent selects Pi
- **WHEN** the selected Agent is configured for the Pi runtime
- **THEN** the invocation MUST execute through Pi using the Agent's snapshotted model, variant, and Skills

#### Scenario: Workflow input attempts to override Agent execution
- **WHEN** a task supplies an unsupported runtime or Agent configuration field
- **THEN** the Action MUST reject the input and MUST NOT override the selected Agent definition

### Requirement: Invocation lineage and idempotent materialization
Each accepted `mohist/agent` invocation MUST create exactly one stable AgentJob and one independent AgentSession. The Job MUST reference the WorkflowRun and task attempt, the selected Agent, and the AgentSession. The Session MUST reference the Workflow origin and Job. The accepted Session MUST contain one initial SessionInput and one initial AgentTurn, and the Job, Session, Input, and Turn identifiers MUST be mutually locatable from Workflow and Agent read surfaces. Replaying the same accepted task attempt MUST return the existing lineage rather than create a second Job, Session, Input, or Turn.

#### Scenario: A task is accepted
- **WHEN** a valid task resolves an active Agent and passes acceptance
- **THEN** the resulting Workflow observation MUST contain stable Job, Session, Input, and Turn identifiers, and each identifier MUST resolve to the same invocation

#### Scenario: Session query follows Workflow lineage
- **WHEN** a caller queries the AgentSession created by a Workflow Action
- **THEN** the Session projection MUST expose enough stable WorkflowRun, task, and AgentJob lineage to identify its originating invocation without reading runtime transcript content

#### Scenario: Dispatch is redelivered after a lost response
- **WHEN** the same accepted task attempt is delivered again after a transport or process interruption
- **THEN** the system MUST reuse the original Job, Session, Input, and Turn identities and MUST NOT submit a duplicate Agent execution

### Requirement: Immutable invocation snapshot
Before an invocation becomes an accepted queued execution, the system MUST persist the selected Agent identity, instructions, runtime, model, variant, Skills, accepted prompt and Workflow context, and resolved workspace identity as one immutable execution snapshot. Queueing, Runner redelivery, result reconciliation, and recovery for that invocation MUST use the snapshot. Editing, renaming, archiving, or reconfiguring the Agent or changing mutable Workflow inputs after acceptance MUST NOT alter queued or running work. A new invocation MUST obtain a new snapshot.

#### Scenario: Agent configuration changes while queued
- **WHEN** the Agent's instructions, runtime, model, variant, or Skills are edited after the Workflow invocation is accepted but before Runner execution
- **THEN** the queued invocation MUST execute with the original snapshot

#### Scenario: Agent configuration changes while executing
- **WHEN** the Agent is renamed, archived, or reconfigured while the invocation is executing
- **THEN** the current Job and Session MUST continue with the original Agent identity and execution definition

#### Scenario: A later invocation starts after an Agent edit
- **WHEN** a new Workflow invocation is created after the Agent definition changes
- **THEN** the new invocation MUST use the new definition without changing the earlier invocation

### Requirement: Shared Agent readiness and admission
The Workflow Action MUST use the same Agent readiness, workspace, Agent concurrency, and Runner capacity decisions as a direct Agent launch. An Agent that has a direct-launch readiness failure MUST be rejected with the same actionable readiness reason before external execution. An unresolved but nonterminal readiness or capacity condition MUST remain observable as queued or recovering work, MUST NOT be reported as successful, and MUST NOT create a parallel Workflow-only admission queue. AgentJobs created by Workflow Actions MUST participate in the same per-Agent concurrency permits as direct AgentJobs and follow-up work.

#### Scenario: An Agent needs setup
- **WHEN** the selected Agent lacks a required instruction, model, or valid runtime configuration
- **THEN** the Workflow Action MUST reject the invocation with the canonical readiness gaps and MUST NOT start AgentJob Runner work or external execution; any transient handoff transport claim MUST be retired as a rejected acknowledgement

#### Scenario: Agent concurrency is full
- **WHEN** the selected Agent has reached its configured concurrent execution limit
- **THEN** the Workflow invocation MUST remain queued with the canonical capacity reason and MUST NOT execute until the shared Agent permit is granted

#### Scenario: Workflow and direct Agent work compete
- **WHEN** a Workflow AgentJob and a direct AgentJob are admitted for the same Agent
- **THEN** both MUST use the same concurrency gate and the number of executing jobs MUST NOT exceed the Agent limit

#### Scenario: Runner capacity is unavailable
- **WHEN** no eligible Runner has a live slot for the accepted AgentJob
- **THEN** the Job MUST remain queued or transition to the canonical unavailable-runner failure according to the direct AgentJob deadline, without bypassing Runner admission or starting a local process

### Requirement: Canonical workspace and execution context
The Workflow invocation MUST resolve its workspace through the canonical Agent launch rules for its Workflow origin, persist the resolved workspace identity before acceptance, and use that identity for later materialization and recovery. It MUST NOT substitute a raw Runner directory, a mutable default, or an unrelated workspace after acceptance. The permitted Workflow context MUST be frozen with the invocation and MUST be the only context supplied to the Agent execution.

#### Scenario: A Workflow workspace is materialized later
- **WHEN** an accepted invocation has a resolved named workspace but the workspace directory is not yet materialized
- **THEN** the Runner MUST materialize or recover that named workspace using the persisted identity, without changing the Job's workspace binding

#### Scenario: The workspace home Runner is unavailable
- **WHEN** the workspace's current home Runner is offline before execution
- **THEN** admission MUST apply the direct Agent workspace-affinity and fallback rules, and MUST expose a stable waiting or failure reason rather than silently using an arbitrary path

### Requirement: Stable lifecycle and recovery projection
The Workflow and Agent read projections for an accepted invocation MUST expose the stable status vocabulary `queued`, `executing`, `completed`, `failed`, `cancelled`, and `recovering`. An accepted invocation MUST begin as `queued`, become `executing` only after AgentJob execution is admitted and claimed, and enter exactly one terminal status. `recovering` MUST identify nonterminal reconciliation of dispatch, Runner loss, result delivery, Session delivery, or another uncertain execution fact. Recovery MUST NOT replay the prompt or synthesize a completed or failed result without authoritative evidence. A terminal status and its result MUST be immutable; late reports MUST be acknowledged as stale and MUST NOT rewrite it.

#### Scenario: A queued Job starts
- **WHEN** shared readiness, workspace, concurrency, and Runner admission succeed and the AgentJob is claimed
- **THEN** both Workflow and Agent projections MUST report `executing`

#### Scenario: A Runner or delivery fact is uncertain
- **WHEN** the system cannot confirm whether the original Agent execution was accepted or whether its result was delivered
- **THEN** the invocation MUST report `recovering`, preserve its Job, Session, Input, and Turn identities, and MUST NOT automatically submit a duplicate prompt

#### Scenario: The Agent completes
- **WHEN** the AgentJob receives an authoritative successful terminal result
- **THEN** the invocation MUST report `completed` with the stable final result and MUST remain completed across replay or later Agent edits

#### Scenario: The Agent fails or is cancelled
- **WHEN** the AgentJob receives an authoritative failure or cancellation, or its bounded admission deadline expires
- **THEN** the invocation MUST report `failed` or `cancelled` with a stable reason and MUST release its shared admission resources exactly once

### Requirement: Stable result and Workflow arbitration
The Action result returned to the Workflow MUST contain stable status, Job, Session, Input, and Turn identifiers, and a stable final result or reason when terminal. Nonterminal results MUST NOT fabricate final output. Workflow task advancement, retry, and recovery decisions MUST consume the AgentJob result projection and remain owned by the Workflow task lifecycle. Session transcript events MUST NOT independently advance the Workflow or decide the AgentJob result. The result contract MUST NOT require Workflow logic to parse internal transcript, Runtime, Runner, or provider payloads.

#### Scenario: A completed result returns to the Workflow
- **WHEN** the AgentJob completes with a structured result
- **THEN** the Workflow task MUST receive that result through its stable Action projection and MUST be able to advance or project variables without parsing Session transcript parts

#### Scenario: A failed result enters Workflow recovery
- **WHEN** the AgentJob fails with a stable failure reason
- **THEN** the Workflow task MUST observe the failure as its Action result and MUST apply its declared retry or recovery policy without changing the AgentJob's terminal result

#### Scenario: A Session event arrives before Job completion
- **WHEN** the AgentSession records activity or transcript facts while the AgentJob remains nonterminal
- **THEN** those facts MUST remain queryable but MUST NOT complete, fail, cancel, or advance the Workflow task by themselves

### Requirement: Unchanged product boundaries
This change MUST NOT change the product semantics of other Workflow Actions, MUST NOT add Slack Bot or general external Agent API behavior, and MUST NOT allow Workflow content to control a Runner process directly. `mohist/agent` MUST remain a task Action and its Workflow-facing contract MUST be the only new entry point covered by this capability.

#### Scenario: An unrelated Workflow Action runs
- **WHEN** a Workflow uses an existing non-Agent Action
- **THEN** that Action MUST retain its existing input, execution, result, and recovery semantics

#### Scenario: Workflow content requests Runner control
- **WHEN** a `mohist/agent` task attempts to pass a Runtime handle, process command, or direct Runner control field
- **THEN** input validation MUST reject it and MUST not expose a Runner process operation
