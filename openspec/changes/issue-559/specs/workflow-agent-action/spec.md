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

The Server SHALL first call the durable coordinator after structural request validation and before live Agent, readiness, or Workspace preflight. The coordinator SHALL key the handoff command by `(projectId, commandId)` and persist the request fingerprint, rendered input, Workflow origin, and a `preflight_pending` phase before resolving mutable Agent state or promoting any participant. A definitive preflight error MUST be persisted with its original code and reason as `rejection_pending` before the Server applies the Workflow failure boundary. Repeating the same command and fingerprint MUST resume that phase or replay the original acknowledgement; it MUST NOT rerun a completed preflight rejection against current Agent state. A conflicting fingerprint MUST return definitive `rejected` with `handoff_conflict`. A lost response, timeout, 5xx response, or `retry` acknowledgement MUST retain the same transport work and resend the same command and fingerprint. Before returning definitive `rejected`, the Server MUST call the lineage-checked `IWorkflowGrain.ApplyAgentHandoffRejectionAsync` operation with the runner, work, TaskRun, command, fingerprint, and structured error; that operation MUST apply the normal Workflow failure/recovery boundary and retire the active handoff exactly once. A transient failure of that operation MUST return `retry`, while the coordinator retains `rejection_pending` for recovery. `accepted` and `rejected` MUST retire the transport obligation. Handoff acknowledgements MUST bypass `/report`, `ReceiveTaskReportAsync`, and `ReceiveCheckReportAsync`.

#### Scenario: Handoff transport is claimed before Agent preflight
- **WHEN** the Runner claims an `agent-handoff` dispatch for a task whose Agent is unavailable
- **THEN** the Server MUST persist the command fence before preflight, the transport claim MAY exist temporarily, but no accepted AgentJob, AgentSession, SessionInput, AgentTurn, external Runtime execution, or Agent concurrency admission MUST be created

#### Scenario: A handoff is accepted
- **WHEN** the handoff command passes Agent selection, readiness, workspace, and Workflow acceptance
- **THEN** the Server MUST return `accepted` with the original command and fingerprint plus the stable Job, Session, Input, and Turn identifiers, and the Runner MUST retire only the transport work

#### Scenario: A handoff response is lost
- **WHEN** the Runner does not receive the response after the Server accepted or rejected the command
- **THEN** the Runner MUST resend the same command identity, fingerprint, and rendered input, and the Server MUST replay the prior acknowledgement without creating another lineage or entering the ordinary Workflow report path

#### Scenario: A definitive handoff rejection fails the Workflow task
- **WHEN** Agent selection, readiness, workspace, or Workflow acceptance returns a definitive rejection
- **THEN** the Server MUST persist the original rejection, apply the structured failure through `ApplyAgentHandoffRejectionAsync`, including the declared Workflow recovery boundary, and persist the terminal `rejected` acknowledgement before returning it; replay MUST return that original acknowledgement without rerunning preflight or applying the failure twice, even if the Agent becomes available

### Requirement: Agent selection and acceptance
At execution time, the Action SHALL resolve the Agent within the Workflow Project using the canonical reference rule: a reference beginning with `agent_` MUST resolve by id only; every other reference MUST resolve by name first and by id only when no name matches. The resolved Agent MUST be active and retain a stable Agent identity for the invocation. A missing, archived, or otherwise unresolvable Agent MUST produce the structured `agent_not_found` failure through the handoff rejection operation and MUST NOT create accepted Agent execution or external Runner work.

#### Scenario: An Agent is selected by name
- **WHEN** a task references an active Agent by name
- **THEN** the invocation MUST use that Agent's stable id and MUST create no fallback runtime execution for another Agent

#### Scenario: An id-like reference is selected by id
- **WHEN** a task references `agent_<id>` and an Agent with that id exists
- **THEN** the invocation MUST select that id without treating the value as a display name

#### Scenario: A referenced Agent is unavailable at dispatch
- **WHEN** a pending task is dispatched after its referenced Agent is archived, deleted, or cannot be resolved
- **THEN** the task MUST durably fence the original `agent_not_found` rejection, fail with that stable reason, and no accepted AgentJob, AgentSession, SessionInput, AgentTurn, external Runtime execution, or Agent concurrency admission MUST be created; any transient handoff transport claim MUST be retired as a rejected acknowledgement that replays the same reason

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
Each accepted `mohist/agent` invocation MUST create exactly one stable AgentJob and one independent AgentSession. The coordinator MUST prepare and verify the complete nonclaimable participant set before Workflow accepts the invocation. The Job MUST reference the WorkflowRun and task attempt, the selected Agent, and the AgentSession. The Session MUST reference the Workflow origin and Job. The accepted Session MUST contain one initial SessionInput and one initial AgentTurn, and the Job, Session, Input, and Turn identifiers MUST be mutually locatable from Workflow and Agent read surfaces. Replaying the same accepted task attempt MUST return the existing lineage rather than create a second Job, Session, Input, or Turn. A failure before Workflow acceptance MUST abort the provisional set.

After Workflow acceptance, the coordinator MUST persist and acknowledge Session acceptance before issuing one final idempotent `IAgentJobGrain.ActivateWorkflowLaunchAsync(commandId)` command. Session acceptance MUST make the existing lineage queryable but MUST NOT open a Runtime or create claimable work. AgentJob activation MUST own the complete transition into canonical admission and return `Activated`, `AlreadyActivated`, `Retry`, or `RejectedBeforeActivation`; it MUST NOT return a definitive rejection after the Job is claimable, holds an Agent permit, or has an admission/dispatch ledger. A lost or uncertain activation response MUST replay the same command, including when the Job was already claimed, and MUST NOT fail Workflow by inference. A definitive pre-activation failure MUST terminalize the same accepted AgentJob as `Failed`, make the AgentSession lineage queryable with the stable reason, release any provisional admission resource, and deliver Workflow failure/recovery only through `PendingWorkflowTerminalDelivery`. No separate post-acceptance Workflow failure operation MAY leave the Job live or bypass its terminal result.

#### Scenario: A task is accepted
- **WHEN** a valid task resolves an active Agent and passes acceptance
- **THEN** the resulting Workflow observation MUST contain stable Job, Session, Input, and Turn identifiers, and each identifier MUST resolve to the same invocation

#### Scenario: Session query follows Workflow lineage
- **WHEN** a caller queries the AgentSession created by a Workflow Action
- **THEN** the Session projection MUST expose enough stable WorkflowRun, task, and AgentJob lineage to identify its originating invocation without reading runtime transcript content

#### Scenario: Dispatch is redelivered after a lost response
- **WHEN** the same accepted task attempt is delivered again after a transport or process interruption
- **THEN** the system MUST reuse the original Job, Session, Input, and Turn identities and MUST NOT submit a duplicate Agent execution

#### Scenario: AgentJob activation response is lost after claim
- **WHEN** AgentJob activation made the Job claimable or executing but the coordinator did not receive its acknowledgement
- **THEN** the coordinator MUST replay the same activation command until AgentJob returns `AlreadyActivated`, and MUST NOT fail the Workflow task, submit another Job, or release the live invocation

#### Scenario: Activation is rejected before AgentJob admission
- **WHEN** Session acceptance or AgentJob activation returns a definitive failure before the Job becomes claimable
- **THEN** the same accepted AgentJob MUST become terminal `Failed`, the Session lineage MUST remain queryable, no external Runtime work may start, provisional admission resources MUST be released, and the normal AgentJob terminal delivery MUST apply Workflow failure or recovery exactly once

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

### Requirement: Workflow Agent completion finalization bridge
An accepted Workflow AgentJob dispatch MUST carry a complete immutable `WorkflowTaskContract` containing `workflowRunId`, `taskRunId`, `taskAttempt`, `jobId`, `sessionId`, `inputId`, `turnId`, `finalizerKey`, `expect`, `artifacts`, `outputs`, `setVars`, and the declared recovery fields. `finalizerKey` MUST be the SHA-256 of canonical JSON for `(workflowRunId, taskRunId, taskAttempt, jobId)`, so it is deterministic for the accepted attempt. The authoritative `WorkflowAgentFinalizer` SHALL be Server-side and Workflow-owned. The Runner MAY evaluate workspace-dependent completion and capture artifacts, but it MUST NOT apply Workflow variables, send an ordinary Workflow task report, or advance WorkflowRun directly.

The Runner completion adapter MUST evaluate `expect` against the structured Agent result and the persisted workspace, derive `_output` from the final assistant text in that result, and upload artifacts through `POST /api/runner/{runnerId}/agent-job-workflow-artifacts` with `workflowRunId`, `taskRunId`, `jobId`, `finalizerKey`, path, content type, content hash, and size. The Server artifact resolver MUST validate the immutable WorkflowAgentInvocation and Job identity without requiring active Workflow Runner work, persist the invocation key on the pending upload, compute a SHA-256 content hash when the request omits one, and make repeated uploads with the same `(finalizerKey, path, contentHash)` idempotent.

The completion adapter MUST transmit the typed finalization envelope through the existing `POST /api/runner/{runnerId}/report` AgentJob branch as the optional `workflowFinalization` field of `RunnerReportRequest`/`WorkResult`, alongside the generic status and AgentJob identity. Direct AgentJob reports MUST omit this field and retain their existing valid-report contract. The Server MUST validate the field before applying the requested terminal result: a Workflow-owned Job requires exactly one envelope whose lineage, `finalizerKey`, terminal status, captured-output shape, `setVars` shape, and artifact ids match its frozen WorkflowTaskContract. For the active runner/work identity, a missing or mismatched Workflow envelope MUST terminalize the same Job as `Failed` with `invalid-workflow-finalization`, construct the finalization request from the frozen contract without untrusted artifact or variable effects, release admission, and stage normal Workflow terminal delivery. A Workflow envelope on a direct AgentJob MUST terminalize that Job with `invalid-agent-job-report`; valid direct reports MUST remain unchanged.

The AgentJob report response MUST contain `disposition = accepted | stale | retry | rejected | conflict`. `accepted` MUST mean the first terminal result is durable. `stale` MUST settle a report that no longer owns active work or repeats an existing terminal payload; a same-payload replay MUST re-emit pending delivery. `rejected` MUST mean the active Job was durably terminalized with the stable invalid-report failure. `conflict` MUST settle a different payload for an already-terminal Job without rewriting it. The Runner MUST remove `awaitingAck` only for those four settled dispositions. `retry`, timeout, 5xx, a missing/unknown disposition, or a malformed acknowledgement MUST retain and resend the identical report body. `ServerConnection.report` and `RunnerHost.reportOnce` MUST inspect this body; HTTP 200 alone MUST NOT settle AgentJob work. Ordinary Workflow report acknowledgement behavior MUST remain unchanged.

Before applying Workflow task completion, the Server-side finalizer MUST call `IWorkflowArtifactBindService.BindAgentInvocationAsync(WorkflowAgentArtifactBindRequest)` with `(workflowRunId, taskRunId, jobId, finalizerKey, artifactUploadIds, declaredArtifacts, projectId, issueNumber)`. The bind operation MUST verify every upload belongs to that immutable invocation and finalizer key, create the visible WorkflowArtifact rows for the TaskRun, write a durable `WorkflowAgentArtifactBindReceipt` keyed by `(workflowRunId, taskRunId, jobId, finalizerKey)`, and remove the pending uploads in the same database transaction. The bind receipt MUST contain the request fingerprint and bound artifact ids, including an empty result when no uploads exist. A same-fingerprint replay MUST return the receipt's ids; a conflicting payload MUST return `artifact_bind_conflict`. It MUST NOT derive identity from active Workflow work or an AgentJob `workId`.

The AgentJob terminal result MUST carry one typed `WorkflowAgentFinalizationRequest` containing the complete lineage, `finalizerKey`, terminal status/reason/output, captured outputs, artifact upload ids, and extracted `setVars`. A Runner-originated terminal report carries it in the optional `workflowFinalization` field described above; a server-generated terminal transition MUST construct the same request from the frozen WorkflowTaskContract and stable failure reason. AgentJob terminal state MUST copy that request verbatim into one `PendingWorkflowTerminalDelivery` for the Workflow-owned path. Durable delivery SHALL invoke only `IWorkflowGrain.ApplyAgentJobFinalizationAsync(PendingWorkflowTerminalDelivery)`, which returns `WorkflowFinalizationAck = Accepted | Stale | Retry | Conflict`.

`ApplyAgentJobFinalizationAsync` MUST verify every lineage field and TaskRun applicability, then persist a `WorkflowAgentFinalizationProgress` with the request fingerprint and phase `pending` before calling an external store. The only later phases SHALL be `artifacts_bound` and `variables_applied`. A same-fingerprint replay at an intermediate phase MUST resume that phase; it MUST NOT return `Accepted` or `Stale`. A conflicting fingerprint MUST return `Conflict` without replacing the first process.

The artifact step MUST invoke the invocation-keyed bind operation and persist `artifacts_bound` with its durable bind-receipt key and bound ids. If binding committed before the phase write, replay MUST recover `WorkflowAgentArtifactBindReceipt` and write the missing phase without rebinding. The variable step MUST use `PatchVariablesIfNewAsync(workflowRunId, finalizerKey, vars)`, which MUST atomically write the variable mutation and a `WorkflowVariablePatchReceipt` keyed by `(workflowRunId, finalizerKey)` and fingerprinted from canonical patch JSON, including for an empty patch. A same-fingerprint replay MUST return that receipt, a different patch MUST return `variable_patch_conflict`, and Workflow MUST persist `variables_applied` with the receipt key and fingerprint before continuing.

Only `variables_applied` progress MAY apply Workflow task completion or recovery. TaskRun/Workflow events and the terminal `WorkflowAgentFinalizationReceipt` MUST be written in one Workflow grain commit. The final receipt MUST contain the request fingerprint, artifact bind receipt key and ids, variable patch receipt key and fingerprint, and task-application fingerprint; it MUST NOT exist before all effects complete. A failed final commit MUST expose neither task application nor the receipt and MUST leave replay at `variables_applied`. A lost response after a successful commit MUST replay the receipt as `Stale`; the first completed commit returns `Accepted`. A transient failure at any phase MUST return `Retry` while retaining AgentJob delivery. An artifact, variable, progress, or final-receipt fingerprint conflict MUST return definitive `Conflict` and MUST NOT be retried.

#### Scenario: Workflow task side effects have a durable owner
- **WHEN** the Runner completes an AgentJob for an accepted Workflow Agent invocation
- **THEN** the Runner MUST send the typed finalization envelope with the frozen Workflow contract, and the Server-side WorkflowAgentFinalizer MUST own artifact validation, variable mutation, and Workflow task application

#### Scenario: Workflow finalization report is invalid
- **WHEN** the active AgentJob Runner reports a missing or mismatched `workflowFinalization` envelope
- **THEN** AgentJob MUST terminalize the same Job with `invalid-workflow-finalization` using its frozen contract, stage normal Workflow failure delivery, and return settled `rejected`; the Runner MUST remove that report only after receiving the typed acknowledgement

#### Scenario: AgentJob report acknowledgement is transient or malformed
- **WHEN** `/report` returns `retry`, loses the response, returns 5xx, or returns no recognized disposition
- **THEN** the Runner MUST retain `awaitingAck` and resend the identical AgentJob report rather than treating HTTP success alone as settlement

#### Scenario: A conflicting terminal report arrives
- **WHEN** a different report payload arrives for an AgentJob whose terminal result is already durable
- **THEN** AgentJob MUST return settled `conflict`, preserve the original result and finalization request, and the Runner MUST retire only the conflicting report

#### Scenario: Workflow finalization is replayed
- **WHEN** terminal AgentJob delivery repeats the same finalizer key and payload
- **THEN** the Server MUST resume the stored intermediate phase or replay the terminal finalization receipt, and MUST NOT bind artifacts again, patch variables again, or apply the Workflow task twice

#### Scenario: Artifact binding commits before progress advances
- **WHEN** the process fails after the invocation-keyed artifact bind transaction commits but before `artifacts_bound` progress is persisted
- **THEN** the next delivery MUST read `WorkflowAgentArtifactBindReceipt`, reuse its bound artifact ids, persist the missing phase, and continue without creating duplicate artifacts, deleting unrelated pending uploads, or losing the binding result

#### Scenario: Variable patch commits before progress advances
- **WHEN** the variable mutation and `WorkflowVariablePatchReceipt` commit but the process fails before `variables_applied` progress is persisted
- **THEN** the next delivery MUST replay the variable receipt, persist the missing phase, and continue without applying the patch twice

#### Scenario: Final Workflow commit fails atomically
- **WHEN** the final commit fails while applying the TaskRun and writing `WorkflowAgentFinalizationReceipt`
- **THEN** neither change MUST be visible, progress MUST remain `variables_applied`, and the next delivery MUST retry the same task application

#### Scenario: TaskRun application commits but its response is lost
- **WHEN** Workflow atomically commits TaskRun application and `WorkflowAgentFinalizationReceipt` but AgentJob does not receive the acknowledgement
- **THEN** redelivery MUST return `Stale` from the final receipt and MUST NOT repeat artifact binding, variable mutation, TaskRun application, stage advancement, or recovery

#### Scenario: Finalization arrives after Workflow Runner work was cleared
- **WHEN** the AgentJob finalizer receives artifact or variable effects after the handoff removed the original Workflow Runner assignment
- **THEN** identity resolution MUST use `(workflowRunId, taskRunId, jobId, finalizerKey)` from the immutable invocation and MUST NOT derive TaskRun identity from active work or an AgentJob work id

#### Scenario: Terminal delivery preserves the finalization request
- **WHEN** a Workflow-owned AgentJob terminal result is redelivered after a process or transport interruption
- **THEN** the durable delivery MUST resend the same `PendingWorkflowTerminalDelivery`, including `WorkflowAgentFinalizationRequest`, and MUST stop only on `Accepted`, `Stale`, or definitive `Conflict`; it MUST NOT fall back to a generic Agent result operation

#### Scenario: Workflow artifacts become visible after AgentJob completion
- **WHEN** the finalizer receives artifact upload ids after the original Workflow Runner assignment was cleared
- **THEN** `BindAgentInvocationAsync` MUST bind those uploads to the originating TaskRun using `(workflowRunId, taskRunId, jobId, finalizerKey)`, remove pending rows exactly once, and expose the bound artifacts through the existing Workflow artifact read surface

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
The Action result returned to the Workflow MUST contain stable status, Job, Session, Input, and Turn identifiers, and a stable final result or reason when terminal. Nonterminal results MUST NOT fabricate final output. Workflow task advancement, retry, and recovery decisions MUST consume the AgentJob result projection through the typed WorkflowAgentFinalizationRequest and remain owned by the Workflow task lifecycle. Session transcript events MUST NOT independently advance the Workflow or decide the AgentJob result. The result contract MUST NOT require Workflow logic to parse internal transcript, Runtime, Runner, or provider payloads.

#### Scenario: A completed result returns to the Workflow
- **WHEN** the AgentJob completes with a structured result
- **THEN** the Workflow task MUST receive that result through the durable finalization bridge and MUST be able to advance or project variables without parsing Session transcript parts

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
