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
4. Retire the handoff work after the Server acknowledges either accepted
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
is keyed by `(projectId, commandId)` and stores that fingerprint before any
participant promotion. Repeating the same request returns the original
`accepted` or `rejected` acknowledgement; a conflicting fingerprint is a
definitive `rejected` acknowledgement with `handoff_conflict`. `retry` is a
nonterminal acknowledgement for a transient server condition. A timeout,
connection loss, or 5xx response is treated the same as `retry` and is retried
with the same request body.

`accepted` and `rejected` are terminal transport acknowledgements. The Server
retires the matching Workflow handoff obligation as part of either response,
and the Runner removes it from `awaitingAck`. The Runner never sends an
`agent-handoff` result to `/report`; the route never calls
`ReceiveTaskReportAsync` or `ReceiveCheckReportAsync`. Only the typed handoff
service may acknowledge or retire this work.

The endpoint is implemented by a Server handoff service backed by an extension
of the existing `AgentLaunchCoordinatorGrain`, not by a separate launch
protocol. Its durable plan contains the Workflow origin, TaskRun ID, rendered
prompt, session name, timeout, immutable Workflow task contract, resolved Agent
snapshot, workspace identity, and pre-minted Job/Session/Input/Turn IDs. The
coordinator persists the command and plan before calling another aggregate.

The coordinator advances the participants in this order:

```text
handoff request
  -> resolve active Agent, readiness, and workspace through existing Server services
  -> invoke the coordinator with the rendered envelope and resolved snapshot
  -> persist request fingerprint and all accepted facts
  -> prepare provisional AgentJob with Job/Input/Turn ids
  -> EnsureInitialLaunchAsync on provisional AgentSession
  -> accept lineage on WorkflowRun
  -> promote AgentJob and AgentSession
  -> submit AgentJob to canonical admission
  -> await AgentJob terminal delivery to WorkflowRun
```

The provisional visibility state prevents a partial launch from appearing as
accepted Agent work. If Agent selection, deterministic readiness, Session
creation, or Workflow acceptance fails, the Server handoff rejects the request
before promotion; when provisional participants already exist, the coordinator
aborts them and records the rejection under the same request identity. The
already-claimed transport work is retired, but no accepted AgentJob,
AgentSession, SessionInput, AgentTurn, or external Runtime work is created.

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

The Server handoff service performs the Agent resolution, readiness check, and
workspace snapshot before it invokes `AgentLaunchCoordinatorGrain`. The
coordinator persists those resolved facts and never re-reads mutable Agent or
Project state while replaying the handoff.

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

When AgentJob becomes terminal, it stages a
`PendingWorkflowTerminalDelivery` containing the immutable delivery ID,
WorkflowRun ID, TaskRun ID, Job ID, Session/Input/Turn IDs, terminal status,
stable reason, and structured Agent result. The AgentJob recovery reminder
retries `IWorkflowGrain.ReceiveAgentJobResultAsync` until it receives
`Accepted` or `Stale`. A duplicate terminal report or a late Runner report
cannot rewrite a terminal Job or TaskRun.

The Workflow result command verifies every lineage identity before applying the
result. On success it applies the existing Workflow task completion and
advancement logic. On failure it creates the normal Workflow task failure so
the declared recovery policy can run. A Job `Cancelled` result remains
`cancelled` in the Agent invocation read and is applied to the TaskRun as the
existing Workflow failure boundary because `TaskRunStatus` has no Cancelled
state. The stable cancellation reason is retained in the task error and
invocation result.

Session runtime events, Session Activity, and Session terminal-close events are
queryable audit facts only. They never call the Workflow result command.

### 6. Preserve task-level completion and side effects at the AgentJob boundary

The handoff plan freezes the Workflow task contract separately from the Agent
execution snapshot:

```text
WorkflowTaskContract {
  expect
  artifacts
  setVars
  recovery
  recoveryRemaining
  taskId/title/stage
}
```

The AgentJob dispatch carries this contract and the stable Workflow owner
identity. The AgentJob Runner path executes the Agent runtime directly, as it
already does for direct AgentJobs, then invokes a shared Workflow-task
finalizer for this dispatch kind:

- `expect` is evaluated against the final Agent result and the persisted
  workspace using the existing completion evaluator. `_output` reads the
  structured final assistant text from the AgentJob result, never Session
  transcript content.
- Artifact uploads use the Workflow owner and `(workflowRunId, taskRunId,
  jobId)` as their idempotency scope. Existing AgentJob task logs remain
  AgentJob-owned.
- `setVars` is applied through an idempotent Workflow-scoped projection keyed
  by the same Job/TaskRun pair. A repeated finalizer call is a no-op.
- The finalizer produces one structured `WorkflowAgentResult` in the AgentJob
  terminal report. WorkflowRun consumes it only through the durable terminal
  delivery command.

If a postcondition fails, the AgentJob terminal result is failed with the
stable Action error and the Workflow recovery policy sees the same failure
boundary as an ordinary Action. The finalizer never advances WorkflowRun
itself.

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
  Extend the existing launch coordinator with provisional visibility, one
  persisted command fence, and abort steps before promotion. Keep a durable
  terminal delivery obligation on AgentJob until Workflow acknowledges it.
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
  contract, run one shared finalizer under stable `(jobId, taskRunId)`
  idempotency, and deliver only its structured result to WorkflowRun.
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
   Workflow request fingerprint, provisional participant commands, Workflow
   acceptance command, and terminal result delivery. Add the AgentJob timeout,
   Workflow contract, and terminal-delivery fields with append-only Orleans
   serializer IDs. Add fake clock, Agent, Workspace, Runner, and Runtime
   seams for deterministic tests.
3. **Workflow ownership handoff:** add guarded Workflow grain operations and
   update dispatch candidate/rendering logic so an accepted Agent handoff is no
   longer returned as Workflow Runner work. Keep the logical stage lock until
   the Job result is applied, but remove the Runner assignment and capacity
   accounting immediately after handoff.
4. **Runner handoff and AgentJob execution:** implement virtual Action render /
   validate plus the internal handoff command. Extend AgentJob dispatch with
   the frozen Workflow task contract and implement the idempotent finalizer for
   `expect`, artifacts, and `setVars`. Keep the AgentJob runtime branch above
   ordinary Action resolution.
5. **Result and read projections:** implement the durable AgentJob-to-Workflow
   terminal delivery, Workflow result application, cross-links, status mapping,
   and `WorkflowAgentInvocationRead`. Add tests for every required lifecycle,
   replay, stale-report, and projection scenario in the issue spec.
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
