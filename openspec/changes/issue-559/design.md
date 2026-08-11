## Context

`mohist/agent` is currently a server-owned virtual Action. Profile validation is
handled by `VirtualActionManifests`, but `WorkflowItemTranslator` resolves the
Agent and changes the dispatch into `mohist/opencode` or `mohist/pi`. The
resulting `WorkDispatch` remains `OwnerKind=workflow` with `AgentJobId=null`.
The Runner then executes the selected runtime through the Workflow Action path,
and any AgentSession is found by `(projectId, workflowRunId, sessionName)`.

That path is correct for an Inline Agent, but it bypasses the durable AgentJob
boundary. In particular, it does not participate in the AgentJob ledger,
Agent concurrency gate, AgentJob workspace admission, AgentJob terminal result,
or the direct Agent launch lineage. It also leaves the Workflow Runner work
item alive while the runtime is executing.

Direct Agent launch already provides most of the required primitives:

- `AgentLaunchCoordinatorGrain` persists a request fingerprint, Job/Session/
  Input/Turn identities, and a one-command-at-a-time recovery fence.
- `AgentJobGrain` persists the Agent execution snapshot, owns admission and
  Runner claim, and reports terminal results idempotently.
- `AgentSessionGrain.EnsureInitialLaunchAsync` records the initial Input and
  Turn before dispatch, while `AgentJobGrain` delivers terminal Session facts
  through a durable delivery obligation.
- `DispatchService` and `RunnerGrain` already treat AgentJob work as a second
  owner ledger and combine Workflow and AgentJob work for Runner capacity.

The design therefore changes the ownership boundary only for the
`mohist/agent` virtual Action. The YAML declaration remains a Workflow Action,
but an accepted invocation is handed to AgentJob. Workflow remains the owner of
the TaskRun, task result, retry policy, and stage advancement. AgentJob remains
the owner of Agent execution, admission, Runner assignment, and terminal
result. AgentSession remains the owner of Input, Turn, transcript, Activity,
and Runtime Binding.

## Goals / Non-Goals

**Goals:**

- Keep the `mohist/agent` input contract small: literal `name`, rendered
  `prompt`, optional `session`, and optional `timeout`; tasks only.
- Resolve the active Agent once per accepted TaskRun attempt and persist the
  complete execution snapshot before AgentJob admission.
- Create one idempotent Job/Session/Input/Turn lineage for an accepted
  invocation and make every identifier locatable from Workflow and Agent read
  models.
- Reuse `AgentRefResolver`, `AgentReadinessService`, named Workspace
  resolution, `AgentJobGrain` admission, and the existing AgentJob Runner
  executor instead of creating Workflow-specific queues or Runner controls.
- Keep prompt rendering at the existing Runner execution boundary while making
  the handoff itself durable and replayable.
- Preserve Workflow task-level completion semantics (`expect`, artifacts,
  `setVars`, and recovery) through a Workflow task finalizer attached to the
  AgentJob dispatch.
- Project `queued`, `executing`, `completed`, `failed`, `cancelled`, and
  `recovering` without deriving state from transcript events.
- Keep direct Agent launch, Inline Agent Actions, checks, and unrelated Actions
  unchanged.

**Non-Goals:**

- A general external Agent API or a new public Agent delegation command.
- Slack Bot or Agent Connection behavior.
- Letting Workflow content select a Runtime, model, variant, Skill, Runner, or
  process command.
- Making AgentSession the owner of AgentJob status or making transcript events
  advance a Workflow.
- A second Agent scheduler, queue, Runner process protocol, or workspace
  identity model.
- Changing the semantics of `mohist/opencode`, `mohist/pi`, or other Workflow
  Actions.

## Decisions

### 1. Use a durable Workflow-to-AgentJob handoff

The server keeps `mohist/agent` as a virtual Action manifest. The dispatch
translator no longer resolves it to a runtime Action. Instead it emits a
transient Workflow-owned handoff dispatch with:

```text
WorkType = "agent-handoff"
OwnerKind = "workflow"
Uses = "mohist/agent"
With = raw attempt-snapshot inputs
```

The handoff is not Agent execution and is not a second execution queue. It is a
short-lived, claimable transport work item on the existing Workflow dispatch
path so the Runner can apply the established variable and template rules. A
transport claim may occupy a temporary Runner slot, but it is not an accepted
Agent execution, does not count against Agent concurrency, and does not permit
an external Runtime or process to start. The Runner performs these steps:

1. Resolve Workflow variables and deferred templates from the attempt snapshot.
2. Validate `name`, `prompt`, `session`, and `timeout` against the virtual
   manifest. `name` must be a literal string and the only accepted keys are the
   four declared keys.
3. Send the rendered, validated envelope to an internal Server handoff command
   keyed by `(projectId, workflowRunId, taskRunId)`.
4. After structural request validation, the Server immediately calls the durable
   coordinator to record the command fingerprint, rendered input, Workflow
   origin, and `preflight_pending` phase before resolving live Agent,
   readiness, or Workspace state.
5. Retire the handoff work after the Server acknowledges either accepted
   lineage or a definitive pre-acceptance rejection. A rejected handoff may
   therefore have been claimed as transport, but it has no accepted
   AgentJob/AgentSession lineage and no external execution.

`agent-handoff` is a protocol branch, not a Runner `ActionRegistry` entry. Its
executor renders and validates the virtual Action envelope, sends the internal
Server command, and does not resolve `mohist/agent` as a runtime Action or start
an Action runtime. The virtual manifest remains the Server-owned source for
profile validation.

The Runner does not resolve live Agent state, open a Session, submit a prompt,
start a Runtime, or control a process for this work type. A lost handoff
response is retried with the same task attempt and request fingerprint.

The handoff wire contract is explicit and separate from `/report`:

```text
POST /api/runner/{runnerId}/agent-handoffs

AgentHandoffRequest {
  commandId = WorkDispatch.WorkId
  requestFingerprint
  projectId
  workflowRunId
  taskRunId
  taskAttempt
  workId = WorkDispatch.WorkId
  input = { name, prompt, session?, timeout? }
}

AgentHandoffAck {
  commandId
  requestFingerprint
  disposition = accepted | rejected | retry
  code?
  reason?
  jobId?
  sessionId?
  inputId?
  turnId?
  retryAfterMs?
}
```

`requestFingerprint` is the SHA-256 of canonical JSON containing the project,
WorkflowRun, TaskRun, attempt, and rendered validated input. The coordinator
is keyed by `(projectId, commandId)`. Its durable command phases are
`preflight_pending`, `participants_ready`, `acceptance_pending`,
`activation_pending`, `accepted`, `rejection_pending`, and `rejected`; the
coordinator stores the original rendered input and any terminal rejection
error with the phase. The initial coordinator call persists the fingerprint
and `preflight_pending` phase before live preflight begins. Repeating the same
request resumes the stored phase or returns the original `accepted` or
`rejected` acknowledgement; it never reruns a completed preflight rejection
against current Agent state. A conflicting fingerprint is a definitive
`rejected` acknowledgement with `handoff_conflict`. `retry` is a nonterminal
acknowledgement for a transient server condition. A timeout, connection loss,
or 5xx response is treated the same as `retry` and is retried with the same
request body.

`accepted` and `rejected` are terminal transport acknowledgements. The Server
retires the matching Workflow handoff obligation as part of either response,
and the Runner removes it from `awaitingAck`. The Runner never sends an
`agent-handoff` result to `/report`; the route never calls
`ReceiveTaskReportAsync` or `ReceiveCheckReportAsync`. Only the typed handoff
service may acknowledge or retire this work.

Before returning a definitive `rejected` acknowledgement, the handoff service
must first persist the original rejection error in the coordinator as
`rejection_pending`, then call the lineage-checked
`IWorkflowGrain.ApplyAgentHandoffRejectionAsync` operation with
`(runnerId, workId, taskRunId, commandId, requestFingerprint, ExecutionError)`.
That Workflow-owned operation verifies the active handoff and fingerprint,
applies the normal failed-TaskRun and declared recovery boundary, clears the
active handoff snapshot, and returns `Accepted` or `Stale`. Only after that
operation succeeds does the coordinator persist the terminal `rejected`
acknowledgement containing the original code and reason. A coordinator
recovery reminder resumes `rejection_pending` after a process loss, so a replay
cannot rerun preflight and accept a Job after the Agent becomes available. A
retry of the same command replays the stored rejection without applying the
failure twice; a conflicting fingerprint returns `handoff_conflict` and cannot
mutate the TaskRun. A transient failure of the Workflow operation returns
`retry` and keeps the transport obligation available for replay.

The endpoint is implemented by a Server handoff service backed by an extension
of the existing `AgentLaunchCoordinatorGrain`, not by a separate launch
protocol. Its durable plan contains the Workflow origin, TaskRun ID, rendered
prompt, session name, timeout, immutable Workflow task contract, resolved Agent
snapshot, workspace identity, and pre-minted Job/Session/Input/Turn IDs. The
coordinator persists the command and plan before calling another aggregate.

The coordinator advances the handoff in this order:

```text
handoff request
  -> persist command fingerprint, rendered envelope, and preflight_pending
  -> resolve active Agent, readiness, and workspace through existing Server services
  -> persist the resolved immutable snapshot
  -> prepare provisional AgentJob with Job/Input/Turn ids
  -> EnsureInitialLaunchAsync on provisional AgentSession
  -> promote and verify both participants as handoff-ready and nonclaimable
  -> accept lineage on WorkflowRun
  -> accept the AgentSession for the Workflow lineage without opening a Runtime
  -> atomically activate AgentJob as the final command and enter canonical admission
  -> await AgentJob terminal delivery to WorkflowRun
```

The coordinator persists each phase and participant command before invoking the
next aggregate. Handoff-ready participants have durable, mutually locatable
identities, but remain hidden from public accepted-invocation reads, outside the
claimable AgentJob set, outside Agent concurrency admission, and unable to
start a Runtime until Workflow acceptance succeeds. This promotion-and-verify
step occurs before Workflow accepts the lineage, so a failed Workflow
acceptance can abort the complete provisional set without leaving a Running
TaskRun pointing at missing participants. If Agent selection, deterministic
readiness, workspace resolution, Session creation, or participant promotion
fails, the coordinator records the original rejection and aborts any
provisional participants under the same command fence before applying the
Workflow failure boundary. The already-claimed transport work is retired, and
no accepted AgentJob, AgentSession, SessionInput, AgentTurn, Agent concurrency
permit, or external Runtime work is created.

Workflow acceptance is idempotent and is attempted only from
`participants_ready`. After it succeeds, the coordinator enters
`activation_pending` and persists one pending participant command at a time.
It first calls the idempotent Session acceptance command, which makes the
pre-existing Session/Input/Turn lineage queryable for the accepted Workflow
invocation but cannot open a Runtime or create claimable work. Only after that
acknowledgement does it issue the final
`IAgentJobGrain.ActivateWorkflowLaunchAsync(commandId)` command.

`ActivateWorkflowLaunchAsync` owns the complete nonclaimable-to-admission
transition. It returns `Activated`, `AlreadyActivated`, `Retry`, or
`RejectedBeforeActivation`. `Activated` and `AlreadyActivated` mean the Job's
durable launch-ready/admission obligation exists; after either response the
command can never become rejected. The operation must not return
`RejectedBeforeActivation` after it has made the Job claimable, acquired an
Agent permit, or written an admission/dispatch ledger entry. A lost response,
including one after the Job was claimed, remains `activation_pending` and
replays the same command until AgentJob returns `AlreadyActivated`; it never
applies a Workflow failure by inference.

A definitive Session acceptance failure or `RejectedBeforeActivation` is
recorded under the coordinator command fence. The coordinator then calls
idempotent participant failure commands: AgentSession makes the accepted
lineage queryable with the stable activation failure, and AgentJob enters the
terminal `Failed` state with `workflow_agent_activation_failed`, confirms that
no claimable ledger or external execution exists, releases any provisional
admission resource, and stages the normal
`PendingWorkflowTerminalDelivery` from the frozen Workflow task contract. That
terminal delivery, not a separate Workflow activation-failure operation,
applies the TaskRun failure/recovery boundary. Once Workflow has accepted the
lineage, every success or failure therefore remains authoritative on the same
AgentJob and cannot leave a failed TaskRun beside a live Job.

**Alternative considered:** resolve the Agent in
`WorkflowItemTranslator` and continue sending `mohist/opencode` or
`mohist/pi`.

Rejected because it keeps TaskRun as the execution owner, creates no
AgentJob-owned admission record, and permits the Workflow Runner path to drift
from direct Agent launch behavior. It is the current implementation and is the
behavior this change replaces.

**Alternative considered:** create the AgentJob before Runner rendering by
expanding Workflow templates on the Server.

Rejected because `design/workflow/actions.md` makes the Runner the authoritative
renderer and validator for deferred Action inputs. The handoff preserves that
boundary and moves only the validated execution intent across it.

### 2. Make TaskRun the Workflow-side lineage record, not the execution owner

An accepted TaskRun receives one immutable `WorkflowAgentInvocation` record:

```text
WorkflowAgentInvocation {
  workflowRunId
  taskRunId
  taskAttempt
  requestFingerprint
  agentRef
  agentId
  sessionName
  jobId
  sessionId
  inputId
  turnId
  application = pending | applied | stale
}
```

The record stores lineage and the terminal-application acknowledgement only. It
does not copy AgentJob status, Runner assignment, Runtime Binding, transcript,
or final output. The AgentJob is the sole source for those execution facts.

At handoff acceptance, WorkflowRun atomically records the invocation, marks the
TaskRun `Running`, and keeps the stage's logical lock. It clears the Runner
assignment and removes the TaskRun from the Workflow dispatch ledger before the
handoff acknowledgement is returned. A handed-off TaskRun is therefore not
counted as active Workflow work, does not consume a Workflow Runner slot, and
cannot be claimed again by `DispatchService`.

The Workflow grain adds guarded operations for:

- accepting or replaying a handoff for the current `(taskRunId, fingerprint)`;
- applying one AgentJob terminal result for the recorded `jobId` and `turnId`;
- returning a stale acknowledgement when the TaskRun is already terminal or
  the same result was already applied.

The existing `ReceiveTaskReportAsync` path remains for ordinary Workflow work.
The AgentJob result bridge never fabricates a Runner task report from a
transcript event.

**Alternative considered:** leave the Workflow task assigned to the original
Runner while the AgentJob runs.

Rejected because the Workflow ledger would redeliver or retain a second active
work item, the original Runner assignment would consume capacity, and a
different Runner could execute the AgentJob concurrently. A handoff must end
the Workflow dispatch obligation before AgentJob admission is allowed to run.

### 3. Reuse the canonical Agent launch snapshot and admission

The workflow handoff uses the same resolution seams as direct launch:

- `AgentRefResolver.ResolveAsync` enforces `agent_*` ID-only resolution and
  name-first fallback for other references.
- The launch snapshot resolver returns the selected Agent ID and name together
  with Instructions, Runtime, Model, Variant, ordered Skills, Agent config,
  and allowed subagents. The resolver validates the config before the plan is
  accepted.
- `AgentReadinessService` rejects deterministic `NeedsSetup` gaps with the
  canonical gap codes before Job/Session promotion. `Unknown` readiness is
  preserved as nonterminal availability and does not become success or failure
  by inference.
- The Workflow origin resolver derives the same named Workspace and repository
  snapshot used by the existing Workflow dispatch context. The plan persists
  the named identity and the resolved path only as the canonical launch
  snapshot; it never accepts a raw Runner default or a caller-provided
  replacement path.
- `AgentJobGrain` acquires the existing per-Agent concurrency permit, applies
  Workspace home affinity and Runner capacity, persists the dispatch ledger,
  and is claimed by the same `DispatchService` poll path as direct AgentJobs.

The Server handoff service invokes `AgentLaunchCoordinatorGrain` to persist the
command fence before it performs the Agent resolution, readiness check, and
workspace snapshot. It then supplies those resolved facts to the coordinator,
which persists them and never re-reads mutable Agent or Project state while
replaying the handoff or promoting participants.

The task prompt is an input goal. It is composed with the snapshotted Agent
Instructions by `AgentJobGrain.BuildDispatchAsync` / the AgentJob executor. The
Workflow envelope cannot replace the Agent Runtime, Model, Variant, Skills,
Instructions, or concurrency policy.

`timeout` is persisted as a turn deadline in the Job snapshot and passed to the
selected Runtime. It is separate from the server-side AgentJob report/admission
deadline. An omitted value uses the selected Runtime Action's existing default;
the Job deadline remains bounded so an unreported execution becomes
`recovering`, not an implicit replay.

### 4. Give each accepted task attempt an independent AgentSession

The coordinator always allocates a new canonical `SessionId` for an accepted
`mohist/agent` TaskRun attempt and records one initial SessionInput and one
initial AgentTurn before promoting the Job. This makes the Job/Input/Turn
lineage one-to-one for the accepted attempt and avoids accidentally attaching
an AgentJob to an older Workflow session that happens to have the same name.

`session` retains its existing template and validation semantics, but is stored
as the requested Workflow session label and lookup context; when omitted, the
existing Work ID fallback is used. It is not the physical Session identity and
it does not merge two AgentJob attempts. Inline
`mohist/opencode` and `mohist/pi` keep their existing same-name Workflow
Session continuity.

The Session metadata includes, without transcript parsing:

```text
projectId, workflowRunId, taskRunId, workType=agent,
sessionName, agentJobId, agentId, agentName, stage, issue, epic,
workspaceName/workspacePath
```

The Session initial-launch record and AgentJob input both carry the same
`jobId`, `workflowRunId`, `taskRunId`, `inputId`, and `turnId`. The existing
`WorkflowAgentSessionMetadata` helper is extended to emit the new labels; the
canonical `sessionId` remains the only Session identity.

### 5. Keep AgentJob terminal state authoritative and deliver it durably

AgentJob status maps to the Workflow-facing vocabulary as follows:

| AgentJob fact | Workflow Agent invocation status |
|---|---|
| `Pending` with admission or capacity waiting | `queued` |
| `Running` after Runner claim | `executing` |
| `Unknown` or an unresolved dispatch/result fact | `recovering` |
| `Completed` | `completed` |
| `Failed` | `failed` |
| `Cancelled` | `cancelled` |

The Workflow invocation read assembler joins its immutable lineage record with
the canonical AgentJob read. It does not persist a second status state machine.
The Agent read assembler exposes the same Job/Session/Input/Turn references and
the same mapped status. A nonterminal read has `result=null`; only the
authoritative terminal Job result can populate it.

When AgentJob becomes terminal, it stages one canonical
`PendingWorkflowTerminalDelivery`:

```text
PendingWorkflowTerminalDelivery {
  deliveryId
  workflowRunId, taskRunId, taskAttempt
  jobId, sessionId, inputId, turnId
  terminalStatus
  stableReason
  agentResult
  workflowFinalization = null | WorkflowAgentFinalizationRequest
}
```

For a Workflow-owned AgentJob, `workflowFinalization` is required and is the
same immutable request persisted with the AgentJob terminal result. A
Runner-originated terminal report carries it in the typed `/report` field. A
server-generated terminal transition, such as bounded admission expiry or
runner loss before a valid report, constructs the same request from the frozen
WorkflowTaskContract and stable failure reason. For a direct AgentJob it
remains null and the existing direct-Agent terminal path is unchanged.
`AgentJobGrain` must not accept a Workflow-owned Runner terminal report that
omits or conflicts with the complete finalization request. For the active
runner/work identity it instead terminalizes the same Job as a stable
`invalid-workflow-finalization` failure constructed from the frozen contract,
so a bad report cannot be acknowledged while leaving the Job nonterminal.

Workflow-owned delivery has one operation:
`IWorkflowGrain.ApplyAgentJobFinalizationAsync(PendingWorkflowTerminalDelivery)`.
It returns `WorkflowFinalizationAck = Accepted | Stale | Retry | Conflict`.
`Accepted` records the first application, `Stale` acknowledges a duplicate or
late delivery whose receipt already exists, `Retry` keeps the durable delivery
obligation pending for a transient failure, and `Conflict` terminates retry for
a different payload under an existing delivery or finalizer key. The AgentJob
recovery reminder retries this operation only for `Retry`; it stops on
`Accepted`, `Stale`, or `Conflict`. A duplicate terminal report or a late
Runner report cannot rewrite a terminal Job, its finalization request, or the
Workflow TaskRun.

The finalization operation verifies every lineage identity before applying the
result. On success it applies the existing Workflow task completion and
advancement logic. On failure it creates the normal Workflow task failure so
the declared recovery policy can run. A Job `Cancelled` result remains
`cancelled` in the Agent invocation read and is applied to the TaskRun as the
existing Workflow failure boundary because `TaskRunStatus` has no Cancelled
state. The stable cancellation reason is retained in the task error and
invocation result.

Session runtime events, Session Activity, and Session terminal-close events are
queryable audit facts only. They never call the Workflow result command.

### 6. Finalize Workflow side effects through an explicit AgentJob bridge

The handoff plan freezes the Workflow task contract separately from the Agent
execution snapshot. It carries the complete Workflow owner identity and a
deterministic finalizer key:

```text
WorkflowTaskContract {
  workflowRunId
  taskRunId
  taskAttempt
  jobId
  sessionId
  inputId
  turnId
  finalizerKey = SHA-256(canonical JSON(workflowRunId, taskRunId, taskAttempt, jobId))
  expect
  artifacts
  outputs
  setVars
  recovery
  recoveryRemaining
  taskId/title/stage
}
```

The authoritative `WorkflowAgentFinalizer` is Server-side and Workflow-owned.
The Runner has only a completion adapter for facts that require the AgentJob
workspace. After the Agent runtime returns, that adapter:

- evaluates `expect` with the existing completion evaluator against the
  persisted workspace and structured Agent result; `_output` reads the final
  assistant text supplied by the AgentJob result, never Session transcript
  content;
- captures and uploads declared or dynamic artifacts through
  `POST /api/runner/{runnerId}/agent-job-workflow-artifacts`, carrying
  `(workflowRunId, taskRunId, jobId, finalizerKey, path, contentType,
  contentHash, size)`. The Server-side
  `WorkflowAgentInvocationArtifactResolver` validates the immutable
  WorkflowAgentInvocation and Job identity instead of looking for active
  Workflow work. The pending upload row stores `workflowRunId`, `taskRunId`,
  `jobId`, and `finalizerKey`; uploads are idempotent under
  `(finalizerKey, path, contentHash)`. The Server computes a SHA-256 content
  hash when the Runner does not supply one, so the idempotency key is never
  based on a missing hash; and
- extracts `_output`, declared outputs, and `setVars` values into a typed
  finalization envelope. It does not call the generic Workflow variable patch
  route, send an ordinary Workflow task report, or advance WorkflowRun.

The completion adapter sends that envelope to AgentJob through the existing
AgentJob branch of the Runner report boundary, rather than creating a second
terminal endpoint or using the generic Workflow report path:

```text
POST /api/runner/{runnerId}/report

RunnerReportRequest {
  workId
  ownerKind = agent-job
  agentJobId
  status, message, output, exitCode, artifactUploadIds, error
  workflowFinalization? = WorkflowAgentFinalizationRequest
}
```

`workflowFinalization` is an optional append-only field on the shared C#
`WorkResult`/`RunnerReportRequest` and TypeScript `WorkItemResult` contracts.
The Runner sends it only for a Workflow-owned AgentJob; direct AgentJob reports
omit it and retain their existing generic result behavior. The `/report` route
forwards the typed value to `IAgentJobGrain.ReportResultAsync` as one report
operation. AgentJob validates that a Workflow-owned terminal report contains
the field, that every lineage id and `finalizerKey` matches its frozen
`WorkflowTaskContract`, that the typed terminal status agrees with the generic
status, and that captured outputs, `setVars`, and artifact upload ids have the
declared JSON shapes before it commits the requested terminal transition. For
the active runner/work identity, a missing or mismatched Workflow envelope
causes AgentJob to persist a terminal `Failed` result with
`invalid-workflow-finalization`, build the finalization request from its frozen
contract with no untrusted artifacts or variable effects, release admission,
and stage the normal Workflow delivery. A non-null Workflow envelope on a
direct AgentJob similarly becomes the stable direct-Agent failure
`invalid-agent-job-report`; valid direct reports retain their existing result
path.

The AgentJob branch of `/report` returns one typed acknowledgement:

```text
AgentJobReportAck {
  agentJobId
  workId
  disposition = accepted | stale | retry | rejected | conflict
  reason?
}
```

`accepted` means the first terminal result and any Workflow finalization are
durable. `stale` means this report no longer owns active work or repeats the
same terminal payload; a same-payload terminal replay also re-emits any pending
delivery. `rejected` means the active Job was durably terminalized with the
stable invalid-report failure above. `conflict` means a different payload
arrived after the Job was already terminal and cannot rewrite it. Those four
dispositions are settled and remove the Runner's `awaitingAck` entry. `retry`
means no terminal decision was durably made and the Runner retains the exact
report. A timeout, 5xx response, malformed/unknown acknowledgement, or missing
disposition is also treated as `retry`.

`ServerConnection.report` returns the typed acknowledgement and
`RunnerHost.reportOnce` inspects its disposition; an HTTP 200 alone is not an
acknowledgement. This extends only AgentJob reporting. The ordinary Workflow
report response remains unchanged. A duplicate valid report returns `stale`,
and a different payload under the same terminal Job/finalizer identity returns
settled `conflict`; neither can rewrite the terminal result or Workflow task.

The AgentJob terminal report carries exactly one
`WorkflowAgentFinalizationRequest` when the dispatch has a Workflow owner:

```text
WorkflowAgentFinalizationRequest {
  workflowRunId
  taskRunId
  taskAttempt
  jobId
  sessionId
  inputId
  turnId
  finalizerKey
  result = {
    status = completed | failed | cancelled
    reason?
    output?
    capturedOutputs?
    artifactUploadIds?
    setVars?
  }
}
```

`AgentJobGrain` persists this envelope with the terminal result and copies it
verbatim into `PendingWorkflowTerminalDelivery`. The Server finalizer verifies
every lineage field, then invokes the invocation-specific artifact bind command:

```text
IWorkflowArtifactBindService.BindAgentInvocationAsync(
  WorkflowAgentArtifactBindRequest {
    workflowRunId, taskRunId, jobId, finalizerKey
    artifactUploadIds, declaredArtifacts, projectId, issueNumber
  })
```

The bind resolver verifies every upload against the immutable invocation and
`finalizerKey`. It owns a durable
`WorkflowAgentArtifactBindReceipt` keyed by
`(workflowRunId, taskRunId, jobId, finalizerKey)` whose request fingerprint
includes the ordered upload ids and declared artifact contract. The first bind
creates the visible `WorkflowArtifact` rows, writes the bind receipt with their
stable ids, and removes the matching pending uploads in one database
transaction. The receipt is written even when the upload list is empty. A
unique constraint on the invocation key serializes concurrent first binds; a
same-fingerprint replay returns the stored bound ids, while a conflicting
payload returns `artifact_bind_conflict`. It does not require active Workflow
work or derive TaskRun identity from `workId`.

The Server finalizer then records a
`WorkflowAgentFinalizationReceipt` containing the request fingerprint, the bind
receipt key, bound artifact ids, and variable-patch fingerprint. If the process
fails after the bind transaction commits but before this finalizer receipt is
written, a replay reads the durable bind receipt, completes the finalizer
receipt, and does not create another artifact row or delete unrelated pending
data. If the bind transaction fails before commit, all artifact rows, the bind
receipt, and pending-upload deletion roll back together and the same request is
safe to retry.
It applies `setVars` only through a keyed
`WorkflowRunVariablesStore.PatchVariablesIfNewAsync(workflowRunId,
finalizerKey, vars)` operation and applies the existing Workflow completion or
recovery boundary once.

The same `finalizerKey` and payload replay the existing receipt as `Accepted`
or `Stale` without repeating artifact binding, variable mutation, or task
application. A conflicting payload is rejected as `finalization_conflict`; a
transient storage or Workflow failure returns `Retry` and leaves the delivery
obligation pending. The finalizer never derives facts from Session events and
never changes the AgentJob terminal result. A Runner-side postcondition failure
is reported as the stable failed `WorkflowAgentResult`, so Workflow recovery
sees the same failure boundary as an ordinary Action.

### 7. Make uncertain work recovering and non-replayable

After an AgentJob Runner claim has crossed the external Runtime boundary, loss
of a response is not evidence that the prompt was rejected. The Job keeps the
original Job/Session/Input/Turn and dispatch identities and transitions to the
existing internal `Unknown` state, projected as `recovering`.

For the new Workflow path:

- `Unknown` is removed from the claimable AgentJob set. `DispatchService` must
  not redeliver it as new work.
- Reconciliation queries the original Job work identity and physical Session
  binding. It can accept authoritative running or terminal evidence for that
  identity, but cannot mint a replacement prompt.
- A Runner loss before authoritative acceptance produces the canonical
  `runner-lost` Job failure according to direct AgentJob rules. A response-loss
  or uncertain external effect remains `recovering` until bounded
  reconciliation decides it.
- A terminal Job result is immutable. Late Runner results are acknowledged as
  stale and cannot change the Workflow result or release a newer invocation.

The handoff coordinator has the same rule: a lost response to any participant
command resumes that command by its persisted command ID and never generates a
new Job, Session, Input, Turn, or prompt.

### 8. Read models and API boundaries

Add one internal `WorkflowAgentInvocationRead` shape assembled from the
Workflow lineage record, AgentJob read, and AgentSession initial-launch read:

```text
WorkflowAgentInvocationRead {
  workflowRunId, taskRunId, taskAttempt
  jobId, sessionId, inputId, turnId
  agentId, agentRef, sessionName
  status = queued | executing | completed | failed | cancelled | recovering
  reason
  result = null | structured terminal result
  workflowApplication = pending | applied | stale
  observedAt
}
```

The existing Workflow run/task read surface includes this projection for a
`mohist/agent` TaskRun. The AgentJob and AgentSession read surfaces include
cross-links back to the WorkflowRun and TaskRun. Direct external Agent API
allowlists remain unchanged: internal Runtime Binding, Runner identity,
workspace paths, prompt content, and raw provider payloads are not exposed by
this change.

The projection treats `workflowApplication=pending` as a delivery fact, not a
second execution status. A terminal AgentJob can therefore be visible as
`completed` while Workflow task advancement is waiting for the durable result
delivery acknowledgement. This makes the cross-owner gap visible without
inventing a success or failure.

## Risks / Trade-offs

- [The temporary handoff occupies a Runner slot while the Server commits the
  Agent lineage] -> Keep the handoff bounded and non-executing; clear the
  Workflow assignment before AgentJob promotion. A lost handoff is retried by
  the same task identity and cannot leave two active execution owners.
- [AgentJob, AgentSession, and WorkflowRun still do not share a transaction] ->
  Persist the handoff command before live preflight, retain the original
  rejection in a `rejection_pending` fence, promote and verify nonclaimable
  participants before Workflow acceptance, and resume activation from durable
  phases after a crash. Keep a durable terminal delivery obligation on AgentJob
  until Workflow acknowledges it.
- [A lost Runtime response could duplicate the prompt] -> Transition the
  original Job to `Unknown`/`recovering`, remove it from claimable work, and
  reconcile the original work and effect identities before any retry. Never
  create a replacement Job or Turn automatically.
- [Agent definition edits can race handoff] -> Resolve and persist the Agent
  ID, Instructions, Runtime, Model, Variant, Skills, Agent config, and allowed
  subagents in the coordinator plan before promotion. Later edits affect only
  later TaskRun attempts.
- [Workspace materialization may happen on a later Runner] -> Persist the
  named Workspace identity and repository snapshot, use the existing
  workspace-affinity admission, and let the AgentJob Runner materialize it.
  Never replace it with a raw path or Runner default after acceptance.
- [Workflow `expect`, artifact, and variable side effects cross from a
  Workflow owner to an AgentJob owner] -> Freeze a separate Workflow task
  contract, let the Runner completion adapter prepare workspace facts, and let
  the Server-side WorkflowAgentFinalizer bind invocation-keyed artifacts and
  apply all Workflow effects under the durable `finalizerKey` receipt.
- [Workflow and Agent read surfaces could disagree during terminal delivery] ->
  AgentJob remains the sole execution authority; Workflow stores only lineage
  and application acknowledgement. Assemblers expose both the Job terminal
  fact and the `workflowApplication` state.
- [Existing Workflow session routes assume `(workflowRunId, sessionName)` is
  unique] -> Add `taskRunId` and canonical `sessionId` to the new AgentJob-backed
  projection and make direct ID lookup authoritative. Keep the old same-name
  lookup behavior for Inline Agent sessions.
- [The AgentJob path could regress direct launches] -> Branch only on the new
  `agent-handoff` and existing `agent-job` work types. Keep direct AgentJob
  dispatch free of Workflow task contracts and retain its existing tests.

## Migration Plan

1. **Contract and model layer:** extend the virtual manifest and validator
   tests, add `WorkflowAgentInvocation` and the Workflow task contract, add
   workflow lineage fields to AgentJob/AgentSession snapshots, and add the
   `agent-handoff` dispatch shape to C# and TypeScript contracts. Preserve
   existing `mohist/opencode` and `mohist/pi` shapes.
2. **Durable launch layer:** extend `AgentLaunchCoordinatorGrain` with the
   preflight command fence, durable rejection outcome, handoff phase machine,
   provisional participant commands, participant promotion/verification,
   Workflow acceptance command, activation recovery, and terminal result
   delivery. Add the AgentJob timeout, Workflow contract, and
   terminal-delivery fields with append-only Orleans serializer IDs. Add fake
   clock, Agent, Workspace, Runner, and Runtime seams for deterministic tests.
3. **Workflow ownership handoff:** add guarded Workflow grain operations and
   update dispatch candidate/rendering logic so an accepted Agent handoff is no
   longer returned as Workflow Runner work. Keep the logical stage lock until
   the Job result is applied, but remove the Runner assignment and capacity
   accounting immediately after handoff.
4. **Runner handoff and AgentJob execution:** implement virtual Action render /
   validate plus the typed internal handoff command. Extend AgentJob dispatch
   with the frozen Workflow task contract and finalizer key; implement the
   Runner completion adapter, invocation-keyed artifact upload and bind routes,
   and Server-side WorkflowAgentFinalizer for `expect`, artifact binding, and
   `setVars`. Keep the AgentJob runtime branch above ordinary Action resolution.
5. **Result and read projections:** implement the durable AgentJob-to-Workflow
   terminal delivery, Workflow result application, cross-links, status mapping,
   and `WorkflowAgentInvocationRead`. Add tests for every required lifecycle,
   preflight rejection replay after Agent recovery, participant promotion and
   activation recovery, terminal-delivery replay, stale-report, and projection
   scenario in the issue spec.
6. **Documentation and cleanup:** update `design/agent-execution.md`,
   `design/agent-api.md`, `docs/actions/agent.md`, and `docs/agent-sessions.md`
   to replace the old TaskRun-only description. Remove the old translator
   fallback and Workflow AgentSession reporter use for `mohist/agent` after
   persisted legacy dispatches have drained.

There is no data backfill for completed TaskRuns. During cutover, existing
`mohist/agent` dispatch snapshots that already contain a concrete
`mohist/opencode` or `mohist/pi` Action are allowed to finish through the old
path; new TaskRun attempts use only the handoff path. Rollback stops creation
of new handoffs, lets accepted AgentJobs finish through their canonical ledger,
and retains the terminal bridge until no new Workflow invocation is pending.

## Open Questions

- Should the Workflow read projection be embedded in the existing task DTO, or
  should the first implementation expose a dedicated
  `/workflow-runs/{workflowRunId}/tasks/{taskRunId}/agent` read route and add
  the embedded field in a later API change?
- Should terminal AgentJob output be retained indefinitely with the existing
  Job retention policy, or should the Workflow invocation keep a compact
  result summary after the full AgentJob is archived?
- What is the exact allowed range and default normalization for the
  `mohist/agent` `timeout` value, and how much delivery margin must the
  server-side AgentJob report deadline add to it?
