# Agent Execution Model

This document defines the shared abstraction boundaries for Workflow, Agent, Session, Runner, and
Runtime adapters. Runtime-specific behavior belongs in [`runtimes/`](runtimes/README.md), for example
[`runtimes/opencode.md`](runtimes/opencode.md)。

## Layers

| Layer | Concept | Owner | Authoritative state |
|---|---|---|---|
| Definition | Mohist Agent | Agent context | Identity, Instructions, config, Skills, status |
| Work | TaskRun | Workflow context | Workflow task lifecycle, result, output, recovery |
| Work | AgentJob | Agent context | Lifecycle and result of one Mohist Agent work item |
| Execution contract | Action | Workflow context | `uses` / `with` input-output contract for one work dispatch |
| Session | AgentSession | Session context | Transcript, context, usage, Activity, current Runtime Binding |
| Runtime | Runtime Session | External Runtime | Physical Session and provider execution state |
| Adapter | OpenCodeRuntime, PiRuntime | Runner process | Protocol, process, events, state reconciliation, errors |

An `Inline Agent` is a product usage mode, not another entity or bounded context. It means that a Workflow
TaskRun directly selects a Runtime-specific Action and provides input without resolving a Mohist Agent. An
`Agent Definition Reference` (`uses: mohist/agent`) is also not an entity. The TaskRun executes against a
snapshot of the referenced Mohist Agent definition. Work ownership and Session origin do not change.

See [`../CONTEXT.md`](../CONTEXT.md) for definitions shared across contexts. This document defines only the
lifecycle, ownership, event contract, and module boundaries of these concepts. It does not establish a
second terminology set.

## Call paths

| Path | Work owner | Runner entry point | AgentSession origin |
|---|---|---|---|
| Direct Workflow call | TaskRun | Runtime Action adapter | Workflow |
| Launch Mohist Agent | AgentJob | AgentJob executor | Agent launch (Web, CLI, Agent Connection, event, or mention) |

```text
Workflow: TaskRun -> Runtime Action adapter --+
                                             +-> Runtime adapter -> Runtime Session
Agent: Mohist Agent -> AgentJob executor -----+
```

The two paths share Runner execution capabilities and Session infrastructure, but they do not share a
work owner. TaskRun owns Workflow work. AgentJob owns Mohist Agent work. Each entry point gives the Runtime
adapter an already resolved AgentSession target. Runtime facts are written back to that Session. Shared
Runtime code must not create a Workflow-to-Agent domain dependency.

Web, CLI, Agent Connection, event routing, and comment mentions are only different call origins for the
"Launch Mohist Agent" path. They do not add a third execution path. Interactive clients submit work and
context through [`agent-api.md`](agent-api.md). The Agent context resolves the definition and creates the
AgentJob. The Session context owns the Session. A provider adapter such as a Slack Bot must not snapshot an
Agent, create a Runtime Session, or own a work result. A Session created from an Agent Connection has the
same stable Session ID observation, Transcript, and later-input semantics as a directly launched Session.

## Action semantics

`mohist/opencode` and `mohist/pi` are Runtime-specific Actions. They mean "execute this input with this
Runtime." They do not receive an Agent ID, resolve an Agent name, read an Agent definition, or create an
AgentJob. Therefore, direct use by a Workflow creates an Inline Agent.

`mohist/agent` is the Agent Definition Reference Action. A task uses `with.name` to reference a Mohist Agent
in the Project. At dispatch, the Server application layer resolves the name to an Instructions and config
snapshot. The task executes through the same mechanism as an Inline Agent. This Action is neither a Runtime
alias nor an AgentJob dispatch channel. TaskRun remains the work owner, the Session has a Workflow origin,
and no AgentJob is created. The Workflow domain holds only the name token. The dispatch application layer
resolves it through the Agent read side, so Workflow does not reference Agent domain types. If resolution
fails because the Agent does not exist or is archived, task dispatch fails. Each dispatch resolves again,
so a retry gets the definition that exists at retry time. See the product contract in
[`../docs/actions/agent.md`](../docs/actions/agent.md)。

The AgentJob path must not dispatch through the public Workflow Action contract. After the Agent definition
is resolved and snapshotted, its executor receives an Agent-owned execution request. The Workflow Action
adapter and AgentJob executor may call the same deep Runtime module. The reuse point is the Runtime
implementation, not the Action.

When a manually launched AgentJob omits the Workspace, the CLI or Web entry point resolves the current
Project's default Workspace and writes the actual Workspace identity into the Session and launch response.
Once dispatch provides a Workspace, `workspace.path` must be a non-empty string. A malformed Workspace is
invalid input and must not fall back to the default directory.

## Work lifecycle and Session

TaskRun and AgentJob own these decisions:

- pending / running / terminal state;
- success, failure, and result;
- retry, recovery, or Workflow advancement.

AgentSession owns these facts:

- SessionInput, AgentTurn, replies, tool calls, and Runtime state in order;
- context and usage;
- model / Runtime observations;
- current Activity and Runtime Binding.

The Workflow Action adapter reports the work result to TaskRun. The AgentJob executor reports the work
result to AgentJob. Both report Session facts to AgentSession. An AgentSession event does not advance the
Workflow or make an AgentJob terminal. A work failure can appear as a diagnostic in the Transcript, but the
Session does not decide the work result.

A Session command is not a work dispatch. A Follow-up only appends a SessionInput to an existing
AgentSession. It does not create a TaskRun or AgentJob. The current execution handles it, or it creates a
later AgentTurn in the same Session. Compact and Reset also change only the Session. They do not rotate the
AgentSession ID.

AgentJob references the first SessionInput and AgentTurn created by launch. `Completed` means that this
launch work returned successfully. It does not mean that AgentSession is closed, and it does not make a
semantic completion judgment about the natural-language task. The first reply can be a clarification
question. Later Follow-ups are recorded as new SessionInputs and corresponding AgentTurns. They do not
reopen or rewrite the original AgentJob. Output that needs a business lifecycle must enter Issue / Workflow.
AgentJob must not wait for the complete conversation to end.

Agent launch fixes Instructions, Runtime, Model, Variant, and Skills. Later input in that AgentSession
continues to use them. Mohist applies Agent concurrency and scheduling policy uniformly. An entry point must
not bypass it, and a policy change must not forcibly rewrite an execution that has already started.

## AgentSession model

The AgentSession structure is close to a physical Runtime Session, but it has a stable Mohist identity.

The following is the minimal shape of persisted domain records. It is not a second public read schema.
[`conventions.md`](conventions.md#canonical-sessioninput-and-dispatch-schema) is the sole authority for the
public fields, state enums, and null rules of Input acceptance and dispatch.

```text
AgentSession
  Id
  Source
  WorkDir
  Activity
  Admission = ready | blocked
  Reason
  NextAction
  ContextGeneration
  CurrentBinding?
  ActiveOperation?
  Inputs
  Turns
  Transcript
  Context
  Usage

SessionInput
  Id
  Sequence
  RequestId                 # caller-provided; unique with SessionId
  RequestFingerprint
  Text?
  TurnId
  TurnRelation = new-turn | steer
  ContextGeneration
  Source
  Attachments

SessionInputRequestMap
  SessionId
  RequestId
  RequestFingerprint
  InputId?                 # null for a durable rejection tombstone
  TurnId?                  # null for a durable rejection tombstone
  State
  AcceptanceReason
  NextAction
  ContextGeneration
  Revision

TurnDispatch
  canonical projection = TurnDispatchRead in conventions.md

AgentTurn
  Id
  Sequence
  Status
  Outcome = completed | failed | cancelled | blocked | null
  Reason?
  NextAction
  Result?                   # only the canonical TurnResultRead projection may expose it
  ContextGeneration
  InputIds

RuntimeBinding
  RunnerId
  Runtime
  RuntimeSessionId
  BindingEpoch

ContextBoundary
  Kind = compact | reset | runtime-change | missing-recovery | force-reset | handoff | rebind
  ContextGeneration
  OperationId
  Result = pending | succeeded | failed | unknown
  ObservedAt
```

The public Session, launch, Input/dispatch, and Turn result shapes are the canonical projections
in [`conventions.md`](conventions.md). In particular, `AgentSession.Admission` is the durable
`AgentSessionRead.admission`; there is no second Session safety field.

`SessionOperationFence` is an internal write-side fencing record. It may persist only the data required to
produce [`SessionOperationRead`](conventions.md#canonical-sessionoperationread), plus `SessionId`, the
candidate creation key, and the target runner/runtime required for an external call. It is not a second
read schema. `ContextBoundary` and the operation result are record shapes in existing persisted AgentSession
state, not new business entities. Historical operations are retained by `operationId`, so the original
operation can be queried after response loss. The caller must provide a reusable `operationId`. The Server
must not generate an operation key that the client cannot see.

Commands for Compact, Reset, recovery, handoff, rebind, stop, and force-reset must carry a caller-owned
`operationId`. A steer Follow-up uses its caller-provided `requestId` as the same operation identity. The
same `operationId` is the retry and query identity after response loss. An internal recovery coordinator
must also own and persist this identity before it sends a command. It must not leave the client waiting on
an invisible Server key. Only launch uses two different identity levels. The client provides
`launchRequestId`, and the Server creates `launchOperationId` during the first prepare. Their mapping must
appear in the canonical launch read model. They must not be used interchangeably.

The following invariants apply:

- `Id`, `Source`, and `WorkDir` do not change during the AgentSession lifecycle.
- Session parentage is an optional `SessionParentLink` owned separately from immutable `Source`.
  It can only be established for a newly launched child Session and later detached; it never turns
  an Agent launch Source into another Source. The complete tree contract is
  [`subagents.md`](subagents.md).
- `CurrentBinding` is the current routing fact. It contains the complete `runnerId`, `runtime`,
  `runtimeSessionId`, and monotonically increasing `bindingEpoch`. It can be replaced as one unit, but
  AgentSession does not store physical Session history.
- `Transcript` is one Session record appended in AgentSession order. It is not split by physical Session or
  another child entity.
- `Context` describes the context of the current Runtime Session. It starts empty after a Binding
  replacement. `Transcript` and cumulative `Usage` are not cleared when the Binding is replaced.
- `ContextGeneration` identifies the current logical context. It is completely different from the
  operation fence's `ClaimGeneration`. `ContextGeneration` starts at 1. An ordinary Compact does not
  increment it, but must persist a ContextBoundary and operation result. An ordinary Reset, Runtime change,
  missing-recovery, force-reset, handoff, or rebind starts a new logical context and increments it in the
  same Session transaction. An old Input or Turn keeps the `ContextGeneration` from its creation. It must
  not move to a new context. In this document, an unqualified `generation` means `ClaimGeneration` only. It
  must not mean `ContextGeneration`.
- One AgentSession has at most one Runtime execution at a time. This serialization constraint makes the
  order within the Session Transcript sufficient to represent the conversation.
- One accepted Input is associated with exactly one Turn. A Turn can contain multiple Inputs, but a later
  Input can join the current running Turn only when the Runtime explicitly supports steer. An ordinary later
  Input creates a new Turn. It must not rewrite the `TurnId` of an existing Input.
- Each SessionInput must store the caller-provided `RequestId`. The Server persists
  `SessionInputRequestMap` with a uniqueness constraint on `(SessionId, RequestId)`. The same key and
  fingerprint return the original Input and Turn. The same key with a different fingerprint returns
  `rejected(idempotency_key_reused)` and writes no new record. Response loss and duplicate submission can
  only reread that mapping. Only a different key can create a new Input.
- Capacity limits do not cause an accepted Input to be discarded, overwritten, or assigned a new ID. When
  capacity is insufficient, a new `new-turn` Input is rejected before acceptance. `#382` owns capacity
  claim/release and the capacity view across Sessions.
- The bounded queue limit for one Session is an admission invariant in this design. Its specific value is
  a runtime parameter. This document does not copy the global capacity policy from `#382`.
- AgentSession has at most one `ActiveOperation`. It is the durable operation fence for recovery, Compact,
  Reset, force-reset, handoff, stop, steer, or rebind. It is not a new business entity. It can be cleared
  only after the operation definitely completes or is rejected.
- User input must contain visible text or an explicit attachment. Attachment-only input does not generate
  a hidden Prompt.
- AgentSession has no `completed`, `failed`, `stopped`, or `closed` lifecycle.

`CurrentBinding` can initially be null. The launch coordinator establishes the first launch Binding. The
Runner can receive the initial dispatch only after the Binding and initial Session admission facts have each
committed in their respective aggregate transactions.

## Activity and Transcript

### Activity

AgentSession has only these Activity states:

| Value | Meaning |
|---|---|
| `idle` | There is no non-terminal Turn and no incomplete Session operation or Session operation with an uncertain result. This is the only safe idle state. |
| `active` | At least one Turn is `queued`, `running`, or `outcome_pending`, or a known Session operation is still progressing. |
| `unknown` | The result of a Turn, Input admission, Runtime side effect, Binding, or operation cannot be confirmed. This is not a safe idle state. |

A new AgentSession initially has `idle` Activity. `CurrentBinding` may be null in this state.

The state transitions are:

```text
idle + input accepted                    -> active
active + known execution or operation ends
  + all Turns terminal                   -> idle
active + no runtime execution, no final result -> active (Turn = outcome_pending)
active + acceptance/side effect uncertain -> unknown
unknown + authoritative evidence        -> active | idle | unknown
unknown + explicit force-reset          -> unknown (old Turn remains unknown)
```

`activity` is calculated from the current `ContextGeneration` only. An unknown state from an old
`ContextGeneration` is not merged with a queued, running, or `outcome_pending` state from the current
`ContextGeneration`. The canonical read model still exposes old unknown states through `unresolvedPrevious`,
with `unresolvedPreviousCount` as optional supporting data, and `nextAction`. It returns current-generation
facts separately in `currentContextActivity`.

An execution completion, failure, or cancellation makes the corresponding Turn `terminal` only after the
Server persists the final result. The Session must still check for another non-terminal Turn or operation.
A Runtime process exit, cache reclamation, HTTP timeout, or retained persistence file also does not imply
that the Session is closed.

Every ordinary command that can create a new side effect reads only canonical
`AgentSessionRead.admission`. The Server refreshes this field in the same Session transaction whenever the
current generation, operation, or Binding changes:

```text
currentContextActivity(session) =
  summarize only Turns, inputs, side effects and operations
  whose ContextGeneration == session.ContextGeneration

unresolvedPrevious(session) =
  all unresolved Turns, inputs, side effects and operation results
  whose ContextGeneration < session.ContextGeneration

deriveAdmission(session) =
  if currentContextActivity(session) == idle
     && every current-generation Turn is terminal
     && no current-generation unresolved external side effect
     && session.ActiveOperation == null:
    { admission = ready, reason = null, nextAction = inspect_session }
  else:
    { admission = blocked,
      reason = admissionReason(current-generation facts),
      nextAction = nextActionFor(admissionReason) }
```

`admission=ready` gates an ordinary new Turn, Compact, Reset, and automatic missing recovery. When the
current generation is `outcome_pending` or `unknown`, it prevents a new ID from hiding an unknown side
effect. An unknown state from an old generation moves to `unresolvedPrevious` and stops blocking a new
Input or Turn in the current generation only after an explicit force-reset confirms and supersedes the old
operation and commits the new Context/Binding boundary. Before that boundary commits,
`admission=blocked`. A steer on a known `running` Turn is the only Input exception. It can be accepted only
when the current Runtime explicitly supports steer, the complete Binding still matches, and no operation is
pending. It does not turn an unknown state into safe idle. `force-reset` is an explicit product escape path,
not an ordinary Reset. Its rules are defined later in this document.

### Transcript contract

SessionInput and AgentTurn are child records owned by AgentSession. They are not independently addressable
and mutable aggregates. Session is the only write authority for Input order, Turn ownership, and state
transitions. Transcript remains a flat sequence of Session facts appended in Session order. Input and Turn
IDs provide stable associations only. They do not create a second message tree or physical Session history.

Each accepted Input corresponds to one stable SessionInput. Retrying the same call must not duplicate the
Input. The canonical `turnRelation` of an Input is `new-turn` or `steer`. `new-turn` creates a new Turn.
`steer` associates the Input with the current running Turn. A Turn can have multiple steer Inputs, but each
Input has only one TurnId. After an Input is persisted, it must not be rebound to another Turn. Messages,
reasoning, tools, usage, model, provider retries, compaction, and state facts continue to enter the same
Transcript in occurrence order.

The Server records whether it accepted an Input, whether a Turn is still executing, and whether the Runtime
has created a side effect. `outcome_pending` means that the admission and submission paths are known but no
final result exists. `unknown` means that admission, the side effect, or the result itself cannot be
confirmed. Neither state can safely accept an ordinary new Turn or execution-context operation. Neither can
be replayed automatically.

When an existing Binding is replaced, `session.context_reset` is the user-visible boundary in the
Transcript:

```json
{
  "type": "session.context_reset",
  "payload": {
    "reason": "reset | runtime-change | missing-recovery | force-reset | handoff | rebind",
    "contextGeneration": 2,
    "operationId": "op_...",
    "observedAt": "2026-07-22T10:03:00Z"
  }
}
```

This fact means only that the later Runtime context starts empty. It does not carry an old or new physical
Session ID and does not establish Binding history. It is not written when the first physical Session is
created from no Binding. Reset, Runtime change, missing-recovery, force-reset, handoff, and rebind must
increment `ContextGeneration`, persist ContextBoundary, replace the Binding when required, and write
`session.context_reset` in the same Session transaction. This fact precedes the next `session.input` after
replacement. An ordinary Compact does not replace the Binding or increment `ContextGeneration`, but it must
persist the ContextBoundary and operation result for the same `ContextGeneration`. Input after a successful
Compact boundary continues to record that `ContextGeneration`.

`session.closed` is not part of the target DSL. Ending one execution does not close the Session.
`session.followup_completed` and `session.followup_failed` are also not part of the target DSL. Input and
Turn express admission and execution separately. One Follow-up event must not mix them.

A consumer must not derive current Activity from historical error, completion, or stop facts. Current
Activity is determined by Session state, the latest Runtime evidence, and the operation fence.

## Follow-up and Cancel

Follow-up product semantics retain two paths. When an idle Session receives a Follow-up, it accepts it with
`turnRelation=new-turn` and starts a new Turn. When a Session is known to be `running` and the Runtime
explicitly supports steer, it adds the Input to the current Turn with `turnRelation=steer`. When the Runtime
does not support steer, it places the Input in a later queued Turn in order with `turnRelation=new-turn`.
An ordinary Follow-up is rejected during `outcome_pending`, `unknown`, or a recovery/Context operation. The
Server must not guess whether it belongs to the old Turn or a new Turn.

```text
acceptFollowUp(session, requestId, inputEnvelope):
  require requestId is caller-provided and non-empty
  fingerprint = canonicalFingerprint(inputEnvelope)

  # Distinguish the relation before queue admission. The transaction rechecks this
  # capability and the current Turn before it writes any Session child record.
  turnRelation = new-turn
  if session.currentTurn != null
     and session.currentTurn.status == running
     and Runtime.supportsSteer(session.currentBinding)
     and session.ActiveOperation == null:
    turnRelation = steer

  existing = RequestMap.find(session.id, requestId)
  if existing != null:
    if existing.requestFingerprint != fingerprint:
      return rejected(idempotency_key_reused)
    if existing.State == rejected:
      return readStoredRejection(existing)
    if existing.turnRelation == steer:
      effect = Session.operation(existing.steerOperationId or existing.requestId)
      return readStoredSteerResult(effect)
    if existing.State == unknown:
      return readStoredAcceptanceUnknown(existing)
    return readInputAndTurn(existing.inputId, existing.turnId)

  atomically:
    insert RequestMap(session.id, requestId, fingerprint)
      with unique(session.id, requestId)
    reread current Turn, current binding, and ActiveOperation
    if turnRelation == steer:
      if current Turn is not running
         or not Runtime.supportsSteer(current binding)
         or ActiveOperation != null:
        turnRelation = new-turn
      else:
        materialize one SessionInput with
          turnId = current Turn.id, turnRelation = steer
        operationId = requestId
        create one SessionOperationRead(kind = steer,
          operationId = operationId, targetInputId = new input id,
          targetTurnId = current Turn.id, expectedBinding = current binding,
          bindingAtEffect = current binding, candidateBinding = null,
          phase = steer-queued, outcome = pending, retryAllowed = true,
          nextAction = dispatch_same_steer_operation,
          full owner/lease/deadline/revision fence)
        persist RequestMap, SessionInput, the steer operation, its durable
          effect/outbox and transcript input together
        # Steer has no second Turn, dispatch attempt, or queue entry. The
        # operation row is the replay identity and durable retry record.
        return accepted(inputId, turnId = current Turn.id, turnRelation = steer,
          steerOperationId = operationId, steerStatus = pending,
          steerRetryAllowed = true)

    # Unsupported steer and a non-running Turn use new-turn. Unknown facts are rejected.
    require ActiveOperation == null
    require no current-generation unknown Input, Turn, dispatch or Runtime side effect
    require no current Turn.status in {outcome_pending, unknown}
    if queuedTurnCount(session) >= session.queueLimit:
      persist RequestMap(state = rejected, InputId = null, TurnId = null,
                         AcceptanceReason = queue_full,
                         NextAction = retry_with_new_request_id_after_capacity)
      return rejected(queue_full)
    materialize one SessionInput with turnRelation = new-turn and one new Turn
    persist the mapping, Input, Turn and dispatch record together
  if unique constraint loses a concurrent race:
    return read the winning mapping
  return accepted(inputId, turnId, turnRelation = new-turn)
```

The first launch uses `requestId=launchRequestId` when materializing its first Input; its
`launchOperationId` remains a separate cross-aggregate identity.

`steer` is accepted only when the current Turn is known to be `running`, current Binding capabilities
explicitly support steer, and there is no active operation. It reuses the existing Turn. It does not add to
`queuedTurnCount` or create a second Turn, dispatch attempt, or queue entry. The admission transaction must
persist the Input, Transcript input, `SessionOperationRead(kind=steer)`, and replayable effect/outbox
together. The operationId is the caller-provided `requestId`; no hidden second idempotency key is generated.
The Runtime adapter then maps this operation/fence to OpenCode `session.promptAsync` or Pi `session.steer`.
It must support querying or idempotent replay with the same operation identity.

The Steer command has these return semantics. `accepted` on the first call or a pending retry means that the
durable effect above is committed and `steerRetryAllowed=true`; it does not mean that the provider has
completed. An operation with confirmed `outcome=succeeded` also returns stable `accepted`. After the Runtime
confirms acceptance, the operation becomes `steer-accepted` / `outcome=succeeded`, the Input gets
`steerStatus=accepted`, and the active operation is released. Call or Runtime response loss first retains
the same operation. If the effect can still be replayed with the same identity, the Input can continue to
report `accepted` externally with `steerStatus=unknown` / `pending`. Otherwise, the command returns
`unknown`, the operation gets `retryAllowed=false`, and Session admission remains blocked. A definitive
rejection or reaching the fixed retry/deadline makes the effect `terminal`. It does not rewrite the Input's
`state=accepted`, and it returns stable reason/nextAction. The system must not automatically duplicate the
original steer with a new requestId. When the Runtime does not support steer or the current Turn is not
`running`, the command uses `new-turn`. If the current state includes `outcome_pending`, `unknown`, or an
unknown side effect, it returns `rejected(turn_outcome_unknown)` and the `nextAction` for querying the
original Turn. It must not pretend that the Input is a new Turn.

Only `new-turn` admission checks the bounded queued-Turn limit within a Session. When the limit is reached,
the same Session transaction persists a definitive rejection tombstone and returns `rejected(queue_full)`
without persisting an Input. A later call with the same `(SessionId, requestId)` first finds this tombstone.
The same fingerprint always returns the same rejection. A changed payload always returns
`rejected(idempotency_key_reused)`. It must not be accepted during a later capacity recovery window. The
caller can submit again when capacity is available only with a new requestId. This limit constrains only
queued admission for one Session. `#382` owns max-concurrent-runs, capacity claim/release, and the capacity
view across Sessions. This design does not copy that scheduling policy.

The `new-turn` admission transaction first persists the Input, Turn relation, and canonical dispatch record,
then enqueues asynchronously. The `steer` admission transaction instead first persists the steer
operation/effect defined above and creates no dispatch record. If the event/outbox write definitely fails,
the complete Session admission transaction fails and the Input does not report accepted externally. If the
Session transaction committed but later enqueue returns `definitely-rejected`, the Input remains
`accepted=true`, the Turn remains non-terminal `queued`, and the dispatch records `dispatchStatus=blocked`
and a reason. This blocked value means temporarily retryable. This write must atomically include one unique
`dispatchRetryId`, `dispatchRetryKind`, `dispatchRetryDueAt`, `dispatchRetryState`, the current attempt, a
fixed deadline, and the retry outbox/command/timer. The durable handler must continue coordination from the
due signal. It must not return merely because it sees blocked or leave the Turn permanently queued. When the
enqueue result is uncertain, it retains the same `dispatchAttemptId`, synchronizes
`dispatchStatus=unknown` and `Turn.status=unknown`, and then queries. It must not deliver again under a
different request identity or attempt. No attempt evidence or an undelivered outbox also permits only
unknown, not terminal blocked.

A scheduled Input is a one-time Follow-up delivered only when it is due. At that time, the Server appends an
ordinary `SessionInput` to the target Session through the same admission path. It does not create a new Input
kind, scheduler, or Session terminal state. See the "Scheduled input" section in
[`subagents.md`](subagents.md) for the complete contract.

A Follow-up command needs only three synchronous results. The canonical Input/operation projection returns
the steer effect state separately:

- `accepted`: Mohist durably accepted the SessionInput. It can still be queued.
- `rejected`: Mohist confirmed that it did not accept the Input.
- `unknown`: Acceptance cannot be confirmed. The Input must not be resent automatically.

For steer, `accepted` requires either a durable operation with confirmed success or an effect that remains
replayable. `steerStatus=unknown` means that Runtime acceptance is not confirmed. A client must not interpret
it as provider acceptance. `terminal` is the terminal state of the effect; it does not change an already
accepted Input to rejected. It must include stable reason/nextAction. If no replayable effect remains after
response loss, the synchronous result for the same request must be `unknown`, even when a query can see the
persisted Input. The command must not return `accepted` without a replay path.

The durable handler for a Steer effect has only the following lifecycle. The Runner adapter request,
query, and replay fields, effect identity, and result classifications are authoritative in
[`conventions.md`](conventions.md#durable-steer-adapter-seam). Here, `adapter.apply/query/replay` is the
Server-to-Runner seam. It does not claim that the current Runner implementation has completed this
migration:

```text
reconcileSteer(operationId):
  operation = Session.operation(operationId)
  require operation.kind == steer
  currentFence = Session.reloadCurrentFence(operationId)
  if operation.outcome in {succeeded, rejected, failed, blocked}:
    return the stored canonical Input and operation projection
  require operation.targetInputId and targetTurnId are unchanged
  if targetTurnIsTerminalOrSuperseded(operation):
    return settleSteerTargetTerminal(operation, currentFence,
      cause = targetTurnTerminalCause(operation), adapterAttemptStarted = false)
  if operation.outcome == unknown and operation.retryAllowed == false:
    return the stored unknown projection with nextAction = query_same_steer_or_force_reset
  require Session.turn(operation.targetTurnId).status == running
  require Session.currentBinding == operation.bindingAtEffect
  claim or take over the same operation, incrementing ownerFence and claimGeneration
  token = complete FenceToken(operation, dispatchAttemptId = null,
                              dispatchRetryId = null,
                              expectedBinding = operation.expectedBinding,
                              candidateBinding = null,
                              bindingAtEffect = operation.bindingAtEffect)
  if targetTurnIsTerminalOrSuperseded(operation):
    return settleSteerTargetTerminal(operation, token,
      cause = targetTurnTerminalCause(operation), adapterAttemptStarted = false)
  result = runFenced(token,
    token => adapter.apply(SteerEffectRequest(
      effectId = { sessionId = operation.sessionId, operationId = operation.operationId },
      sessionId = operation.sessionId,
      targetSessionId = operation.sessionId,
      targetInputId = operation.targetInputId,
      targetTurnId = operation.targetTurnId,
      requestFingerprint = operation.requestFingerprint,
      text = Session.inputText(operation.targetInputId),
      binding = operation.bindingAtEffect,
      bindingAtEffect = operation.bindingAtEffect,
      fence = token)),
    result => Session.persistAdapterAttemptOnlyIfFenceMatches(token, result))
  if result == StaleFence:
    current = Session.reloadCurrentOperation(operation.operationId)
    if targetTurnIsTerminalOrSuperseded(current):
      return settleSteerTargetTerminal(current, Session.reloadCurrentFence(current.operationId),
        cause = targetTurnTerminalCause(current), adapterAttemptStarted = true)
    return the reloaded canonical operation/Input projection (unknown or superseded)
  if result == ProviderAccepted:
    atomically:
      current = Session.reloadCurrentOperation(operation.operationId)
      if targetTurnIsTerminalOrSuperseded(current):
        return settleSteerTargetTerminal(current, token,
          cause = targetTurnTerminalCause(current), adapterAttemptStarted = true)
      require fenceMatch(Session, Session.reloadCurrentFence(current.operationId), token, now)
      persist the matching operation as
        phase = steer-accepted, outcome = succeeded, retryAllowed = false,
        Input.steerStatus = accepted, Input.steerNextAction = inspect_turn
      clear this ActiveOperation
  if result == DefinitelyRejected:
    atomically:
      current = Session.reloadCurrentOperation(operation.operationId)
      if targetTurnIsTerminalOrSuperseded(current):
        return settleSteerTargetTerminal(current, token,
          cause = targetTurnTerminalCause(current), adapterAttemptStarted = true)
      require fenceMatch(Session, Session.reloadCurrentFence(current.operationId), token, now)
      persist the matching operation as
        phase = failed, outcome = rejected, retryAllowed = false,
        Input.steerStatus = terminal, Input.steerNextAction = inspect_steer_operation
      clear this ActiveOperation
  if result == ResponseLost or result == Unknown:
    atomically:
      current = Session.reloadCurrentOperation(operation.operationId)
      if targetTurnIsTerminalOrSuperseded(current):
        return settleSteerTargetTerminal(current, token,
          cause = targetTurnTerminalCause(current), adapterAttemptStarted = true)
      require fenceMatch(Session, Session.reloadCurrentFence(current.operationId), token, now)
      persist the same operation as phase = steer-dispatching, outcome = unknown,
        retryAllowed = adapter_can_replay(current),
        Input.steerStatus = unknown,
        Input.steerNextAction = query_same_steer_operation,
        Session.activity = unknown, admission = blocked,
        Session.reason = steer_result_unknown,
        Session.nextAction = query_same_steer_or_force_reset
    return accepted only when retryAllowed == true; otherwise return unknown

reconcileUnknownSteer(operation):
  operation = Session.reloadCurrentOperation(operation.operationId)
  if operation.outcome in {succeeded, rejected, failed, blocked}:
    return the stored canonical projection
  if targetTurnIsTerminalOrSuperseded(operation):
    return settleSteerTargetTerminal(operation, Session.reloadCurrentFence(operation.operationId),
      cause = targetTurnTerminalCause(operation), adapterAttemptStarted = false)
  queryFence = Session.reloadCurrentFence(operation.operationId)
  queryToken = fenceToken(queryFence,
    expectedBinding = queryFence.expectedBinding,
    candidateBinding = null,
    bindingAtEffect = queryFence.bindingAtEffect)
  queryResult = runFenced(queryToken,
    token => adapter.query(SteerEffectQuery(
      effectId = { sessionId = operation.sessionId, operationId = operation.operationId },
      sessionId = operation.sessionId,
      targetSessionId = operation.sessionId,
      targetInputId = operation.targetInputId,
      targetTurnId = operation.targetTurnId,
      requestFingerprint = operation.requestFingerprint,
      binding = operation.bindingAtEffect,
      fence = token)),
    result => Session.persistAdapterAttemptOnlyIfFenceMatches(token, result))
  operation = Session.reloadCurrentOperation(operation.operationId)
  if queryResult == StaleFence:
    if targetTurnIsTerminalOrSuperseded(operation):
      return settleSteerTargetTerminal(operation, Session.reloadCurrentFence(operation.operationId),
        cause = targetTurnTerminalCause(operation), adapterAttemptStarted = true)
    return the reloaded canonical operation/Input projection (unknown or superseded)
  if targetTurnIsTerminalOrSuperseded(operation):
    return settleSteerTargetTerminal(operation, Session.reloadCurrentFence(operation.operationId),
      cause = targetTurnTerminalCause(operation), adapterAttemptStarted = true)
  if queryResult == ProviderAccepted: persist the fenced succeeded projection only if the
    target and complete fence still match; otherwise use the terminal-race settle
  if queryResult == DefinitelyRejected:
    persist the fenced terminal projection with outcome = rejected,
      steerStatus = terminal, retryAllowed = false,
      reason = steer_provider_rejected, nextAction = inspect_steer_operation
  if queryResult == DefinitelyAbsent:
    if operation.retryAllowed == true and now < operation.deadline:
      replayFence = Session.reloadCurrentFence(operation.operationId)
      replayToken = fenceToken(replayFence,
        expectedBinding = replayFence.expectedBinding,
        candidateBinding = null,
        bindingAtEffect = replayFence.bindingAtEffect)
      replayResult = runFenced(replayToken,
        token => adapter.replay(the original SteerEffectRequest with fence = token),
        result => Session.persistAdapterAttemptOnlyIfFenceMatches(token, result))
      reconcile the replay result through the same accepted/rejected/terminal-race branches
    else:
      persist outcome = unknown, retryAllowed = false,
        reason = steer_effect_absent_without_replay_window,
        nextAction = query_same_steer_or_force_reset, admission = blocked
  if queryResult == ResponseLost or queryResult == Unknown:
    retry the same operation only when retryAllowed == true and before deadline;
    otherwise retain outcome = unknown, retryAllowed = false,
      admission = blocked, nextAction = query_same_steer_or_force_reset

settleSteerTargetTerminal(operation, fence, cause, adapterAttemptStarted):
  atomically:
    require operation.kind == steer
    require operation.targetTurnId is unchanged
    require current Turn is terminal
       or a stop/force-reset transaction has superseded this target
    require current operation fence == fence,
      or this is the stop/force-reset transaction that invalidated fence
    if adapterAttemptStarted == false:
      outcome = rejected
      reason = cause
    else:
      outcome = blocked
      reason = steer_target_changed_after_effect_attempt
    persist phase = failed, outcome, retryAllowed = false,
      Input.state = accepted, Input.inputId and Input.turnId unchanged,
      Input.steerStatus = terminal,
      Input.steerNextAction = inspect_steer_operation,
      operation.reason = reason, operation.nextAction = inspect_steer_operation
    clear ActiveOperation only when it is this operation
    increment Session/operation revision
    return the stored terminal projection

targetTurnIsTerminalOrSuperseded(operation):
  return Session.turn(operation.targetTurnId).status == terminal
    or confirmed Stop(operation.targetTurnId) exists
    or force-reset supersedes operation.targetTurnId

targetTurnTerminalCause(operation):
  return target_turn_stopped when confirmed Stop exists
    else target_turn_superseded when force-reset supersedes the target
    else target_turn_terminal

persistSteerAdapterAttemptOnlyIfFenceMatches(token, result):
  atomically:
    if not fenceMatch(Session, Session.reloadCurrentFence(token.operationId), token, now):
      return StaleFence(
        { sessionId = token.sessionId, operationId = token.operationId },
        stale_operation_fence)
    persist only the adapter attempt/result discriminator under this fence
    return result
```

The handler rechecks the complete fence before and after every adapter call. A stale binding or
takeover prevents the old effect write. A terminal Turn, stop, or force-reset invokes the atomic
settle above: before an adapter attempt it is a definitive terminal rejection; after an adapter,
query or replay attempt has started it is terminal `blocked` with a stable reason, because the
provider result is no longer authoritative. Both paths release `ActiveOperation`, set
`retryAllowed=false`, preserve the accepted Input and original Turn identity, and are replay-only;
they cannot loop or report provider accepted. A stale owner never changes `bindingAtEffect` or
constructs a new steer operation. Server restart scans pending/unknown steer operations and runs
this same lookup, claim, query and same-identity replay path. A duplicate request first reads the
request map and operation, so it cannot create a second Input or claim `accepted` without the
durable replay record.

The call idempotency key locates the same SessionInput. It is not another domain entity in AgentSession and
does not group the Transcript. After `unknown`, only the same call identity can be checked or retried.
Resending with a new identity can create duplicate side effects.

Callers of Compact, Reset, recovery, handoff, rebind, stop, and force-reset must explicitly provide an
`operationId`. Steer uses the Follow-up's caller-provided `requestId` as its operationId. A command without a
key is rejected before admission. The grain does not generate a replacement key that the client cannot see.
Replaying the same `operationId` returns the same operation. A different key cannot join or overwrite
another active operation. A completed operation remains queryable by the same key, so response loss does
not open a second operation.

Cancel/stop targets only the current non-terminal Turn and uses one
`SessionOperationRead.kind=stop` as the durable Turn-stop fence. A queued Turn is cancelled in the same
Session transaction that creates this operation. For a running Turn, a durable owner claims the operation
and then asks the Runtime to stop. If the stop result cannot be confirmed, the Turn becomes `unknown` and
Session Activity remains `unknown`. The system must not fabricate `idle`. AgentJob decides the result of the
first Turn. Cancelling a later Turn does not rewrite an already terminal AgentJob.

```text
BeginStop(session, operationId, turnId, expectedRevision, expectedContextGeneration,
          expectedBinding, ownerId, deadline):
  fingerprint = canonicalFingerprint(
    sessionId = session.id, kind = stop, targetTurnId = turnId,
    expectedRevision, expectedContextGeneration,
    expectedBinding, deadline)
  atomically:
    existing = Session.operation(operationId)
    if existing != null:
      require existing.kind == stop
      require existing.targetTurnId == turnId
      require existing.requestFingerprint == fingerprint
      require existing.expectedRevision == expectedRevision
      require existing.expectedContextGeneration == expectedContextGeneration
      require existing.expectedBinding == expectedBinding
      return fullSessionOperationRead(existing)
    require session.revision == expectedRevision
    require session.ContextGeneration == expectedContextGeneration
    require current Turn == turnId and Turn is not terminal
    fence = create durable ActiveOperation(
      kind = stop, operationId, targetTurnId = turnId,
      requestFingerprint = fingerprint,
      expectedRevision = expectedRevision,
      expectedContextGeneration = expectedContextGeneration,
      expectedBinding = expectedBinding, candidateBinding = null,
      bindingAtEffect = expectedBinding, ownerId,
      ownerFence = nextOwnerFence(), claimGeneration = nextClaimGeneration(),
      leaseUntil = leaseFor(deadline), deadline,
      phase = claimed, outcome = pending, reason = null,
      nextAction = stop_turn)
    if Turn is queued:
      cancel its dispatch retry work
      persist TurnDispatch.dispatchStatus = terminal,
        dispatchLastResult = none, retryAllowed = false,
        dispatchRetryId = null, dispatchRetryKind = none,
        dispatchRetryDueAt = null, dispatchRetryState = none,
        dispatchRetryOwnerId = null, dispatchRetryLeaseUntil = null,
        dispatchRetryClaimGeneration = null,
        dispatchFence = null
      persist Turn.status = terminal, Turn.outcome = cancelled, Turn.result = null
      complete fence with outcome = succeeded, reason = null,
        nextAction = inspect_turn
      return cancelled

  if Turn is running:
    fence = claimOrTakeOver(fence, before = deadline)
    preToken = Server.recheckBeforeExternalEffect(fullFenceToken(
      fence, bindingAtEffect = fence.expectedBinding))
    result = Runtime.stop(turnId, fenceToken = preToken)
    Server.recheckBeforePersistingEffectResult(preToken)
    persist stop result only if the same complete preToken still matches
    if result is accepted:
      atomically persist dispatchStatus = terminal, dispatchLastResult = accepted,
        retryAllowed = false, dispatchFence = null,
        Turn.status = terminal, Turn.outcome = cancelled, Turn.result = null,
        operation.phase = completed, operation.outcome = succeeded,
        operation.reason = null, nextAction = inspect_turn
    if result is unknown:
      persistStopUnknownIfFenceMatches(preToken, turnId,
        reason = stop_result_unknown,
        nextAction = query_same_stop_operation_or_attempt)
```

The Session transaction that confirms a queued or running Turn stopped also settles any steer
operation targeting that Turn with `settleSteerTargetTerminal(cause=target_turn_stopped)`. A steer
that has not reached the adapter becomes `outcome=rejected`; a steer whose adapter/query/replay has
started becomes terminal `outcome=blocked`. The stop transaction invalidates the steer fence and
releases its `ActiveOperation` atomically. A late steer result is stale and cannot be persisted as
accepted. An uncertain Runtime stop does not terminalize the Turn, so it leaves both operations
queryable as unknown instead of guessing a terminal result.

```text
persistStopUnknownIfFenceMatches(stopToken, turnId, reason, nextAction):
  atomically:
    require the stop operation fence and target Turn still match stopToken
    require Turn.id == turnId and Turn.status == running
    require TurnDispatch.dispatchAttemptId != null
    if TurnDispatch.dispatchRetryId != null:
      mark DispatchRetryWork(TurnDispatch.dispatchRetryId) = cancelled
    newRevision = nextRevision()
    persist TurnDispatch.dispatchStatus = unknown,
      TurnDispatch.dispatchLastResult = unknown,
      TurnDispatch.retryAllowed = false,
      TurnDispatch.dispatchFence = null,
      TurnDispatch.revision = newRevision,
      Turn.status = unknown, Turn.outcome = null,
      Turn.reason = stop_result_unknown,
      Turn.nextAction = nextAction,
      operation.phase = stopping, operation.outcome = unknown,
      operation.reason = reason, operation.nextAction = nextAction,
      operation.revision = newRevision, session.revision = newRevision
    clear only dispatch retry identity/lease fields; retain the original
      TurnDispatch.dispatchAttemptId for same-attempt query
    return unknown
```

The durable coordinator scans pending/unknown stop operations after restart, takes over an expired
lease by incrementing `ownerFence` and `claimGeneration`, and rechecks the complete `FenceToken`
both before and after `Runtime.stop`. Response-loss query/retry uses the same `operationId`, target
Turn and binding, is bounded by the persisted deadline, and never creates a new stop operation,
dispatch attempt, Input or Turn. It first queries the same stop operation and original
`dispatchAttemptId`; a bounded retry may reuse that operation's provider idempotency identity. If
the query/retry remains unknown at the deadline, the operation and Turn stay `unknown` with the
manual/query next action rather than becoming cancelled or idle.

Compact uses the same before/after fence gates around `Runtime.compact`; neither Runtime effect
may run after lease expiry, binding change, or operation takeover.

## AgentSession origins

Each AgentSession has exactly one immutable origin.

### Workflow origin

Address it by `(projectId, workflowRunId, sessionName)`. Reusing the same name within one WorkflowRun
continues the logical Session. When an explicit name is omitted, use the Work ID so unrelated tasks do not
accidentally share context.

### Agent launch origin

Each Mohist Agent launch creates this origin and associates the resolved Agent ID. One Mohist Agent can
create multiple AgentJobs and AgentSessions. A later Agent edit or archival does not change the Session
origin or the execution snapshot from launch time.

The same Prompt, model, Runtime, Workspace, or config does not merge two origins. A Session cannot migrate
from a Workflow origin to an Agent origin or the reverse.

An origin-specific route is only a query and convenience entry point. It ultimately resolves to the
canonical AgentSession resource identified by `sessionId`. Follow-up, Compact, Reset, Transcript, and
queries all operate on that resource. They must not implement a second Session lifecycle.

## Current Runtime Binding

The AgentSession ID is the stable identity of the logical Session. Runtime Session identity is an external,
physical dimension:

```json
{
  "runnerId": "runner-...",
  "runtime": "opencode",
  "runtimeSessionId": "ses_...",
  "bindingEpoch": 7
}
```

Normal execution, retry, Follow-up, Compact, and Runner restart reuse the current Binding. Reset, Runtime
change, and confirmed Runtime Session missing recovery can replace the complete Binding, but they cannot
change the AgentSession identity, origin, or working directory. Each replacement increments `bindingEpoch`
and uses the complete `runnerId`, `runtime`, `runtimeSessionId`, and `bindingEpoch` as its fence.

AgentSession stores only `CurrentBinding`. An old Binding does not enter the aggregate, DTO, or a separate
query model. The existing Transcript is not split by Binding. Reset, missing recovery, or Runtime change
writes one `session.context_reset` to the Transcript. This says that later Runtime context starts empty. It
does not record physical Session history.

Replacement uses the complete expected Binding in a compare-and-swap:

```text
replaceBinding(expected, candidate, operationId, ownerId, ownerFence,
               claimGeneration, deadline):
  require session.admission == ready unless operation kind is force-reset
  require currentBinding == expected
  require candidate was created for AgentSession.workDir
  preToken = fenceToken(fence,
    operationId = operationId, ownerId, ownerFence, claimGeneration,
    expectedBinding = expected, candidateBinding = candidate,
    bindingAtEffect = expected, deadline)
  { currentBinding, postFence } = compareAndSwapBinding(
    preToken, boundaryKind = reset | runtime-change | missing-recovery |
      force-reset | handoff | rebind)
  # postFence is the only valid token after CAS; it has the new revision and candidate binding.
  return postFence
```

`compareAndSwapBinding` is the single atomic protocol in `conventions.md`. It compares the complete
pre-token and atomically changes `expected -> candidate`, increments `revision` and `bindingEpoch`,
and returns `postFence/currentBinding=candidate`. Every post-CAS phase, input write, Runtime
submit, result write, and completion must use that returned post fence. A pre-token with the old
expected binding cannot authorize a post-CAS write, even when the operationId is unchanged.

Each Runtime command/event must carry the complete runner/runtime/runtimeSessionId/bindingEpoch tuple, the
associated InputId/TurnId, and operationId when applicable. When that tuple does not equal the current
Binding, the Server rejects the event. A late event from an old physical Session, old Runner, or old
operation must not change current Activity, Turn, or Transcript.

The Runtime adapter owns physical Session caching, files, process resources, and retention policy. Binding
replacement does not require Mohist to delete, close, or continue querying the old physical Session.

## Runtime Session missing recovery

Missing recovery repairs the Session's current Binding. It is not a Prompt retry or Workflow recovery.
Confirmed missing can trigger automatic recovery/rebind only when the current generation has no `unknown`
Input, Turn, dispatch, or Runtime effect; has no `running` or `outcome_pending` Turn; has current Activity
`idle`; and has `admission=ready`. If work is running, a result is pending, a fact is unknown, or an old side
effect might have occurred, recovery can only observe/query. It must retain the original Binding and Turn
and write `admission=blocked` with actionable `nextAction=query_runtime_or_force_reset`. It must not rebind
automatically.

### Durable recovery operation

RecoveryWindow is not an in-memory window. It is a durable `SessionOperationFence` in AgentSession, and
one Session has at most one active fence. The only public fields and kind-specific null/required rules are
in [`conventions.md#canonical-sessionoperationread`](conventions.md#canonical-sessionoperationread). The
write side also persists `SessionId`, candidate identity, target runner/runtime, and the cleanup-fence
association. These values provide fencing and do not form a second read schema. `ownerFence`,
`claimGeneration`, `expectedBinding`, `candidateKey`, lease, and deadline must be recoverable after restart.

`claimGeneration` is the raw integer generation of a recovery claim. It is not `ContextGeneration`,
`bindingEpoch`, or a candidate version. If an implementation or command writes only `generation`, it must
mean `claimGeneration`. Public contracts and persisted fields use the complete name. `ownerFence` and
`claimGeneration` can only increase. They must not reset on restart, cleanup, or an operation-result write.

Every durable Recovery write and Runtime/provider side effect uses the sole `FenceToken` and `fenceMatch`
from [`conventions.md#canonical-effect-fence`](conventions.md#canonical-effect-fence). An implementation
must not define a shortened fence in this section or another module. Recovery `resolve`, candidate
`createOrGetEmpty`, `submitInputExactlyOnce`, `recordCandidate`, Binding CAS, and `complete` must follow the
executable order below. Cleanup uses the same order, but its token comes from an independent cleanup fence:

```text
runFenced(token, effect, persistResult):
  token = Server.recheckBeforeExternalEffect(token)
  result = effect(token)
  Server.recheckBeforePersistingEffectResult(token)
  return persistResultIfFenceMatches(token, result)

resolve = runFenced(resolveToken,
  token => Runtime.resolve(expectedBinding, fenceToken = token),
  result => Session.writePhase(resolveToken, result))

candidate = runFenced(createToken,
  token => Runtime.createOrGetEmpty(candidateKey, fenceToken = token),
  result => Session.recordCandidate(createToken, result))

candidateFence = Session.reloadCurrentFence(operationId)
preToken = fenceToken(candidateFence,
  expectedBinding = expectedBinding,
  candidateBinding = candidateFence.candidateBinding,
  bindingAtEffect = expectedBinding)
replace = compareAndSwapBinding(preToken, boundaryKind = missing-recovery)
selected = replace.currentBinding
postToken = replace.postFence

submit = runFenced(postToken,
  token => Runtime.submitInputExactlyOnce(inputId, turnId,
                                          binding = selected, fenceToken = token),
  result => Session.recordSubmit(postToken, result))

completeToken = Session.reloadCurrentFence(operationId)
complete = runFenced(completeToken,
  token => no_external_call(),
  result => Session.completeOperation(completeToken, result))
```

`expectedBinding` and `candidateBinding` are each either a complete tuple or explicit `null`. `sessionId`,
`operationId`, `ownerId`, `ownerFence`, `claimGeneration`, `revision`, `leaseUntil`, and `deadline` must not
be omitted. Runtime/provider must validate the same token at the actual side-effect boundary. When the token
expired, the lease was taken over, or the current Binding changed, it returns `stale_operation_fence` and
does not create, submit, discard, or complete. A persistence failure for a phase write, candidate
`getByKey`, candidate `discardCandidate`, CAS, or complete does not change the facts of the old owner.

Create and claim the fence before candidate creation:

```text
BeginRecovery(expected, operationId, ownerId, deadline):
  fingerprint = canonicalFingerprint(
    sessionId = session.id, kind = recovery, expectedBinding = expected,
    expectedRevision = session.revision,
    expectedContextGeneration = session.ContextGeneration, deadline)
  if existing = Session.operation(operationId) != null:
    require existing.kind == recovery
    require existing.requestFingerprint == fingerprint
    return fullSessionOperationRead(existing)
  if current operation has another operationId and its lease is live:
    return recovery_in_progress
  if the only remaining work is a cleanup fence with a terminal original operation:
    allow a new binding operation; cleanup continues under its own bounded fence
  if current operation deadline < serverNow:
    reconcile candidate; if it is not adopted, transfer cleanup to an independent cleanup fence
    return recovery_expired
  otherwise:
    atomically create the fence with:
      requestFingerprint = fingerprint
      expectedRevision = session.revision
      expectedContextGeneration = session.ContextGeneration
      candidateKey = stableCandidateKey(operationId, recovery)
      targetRunnerId = expected.runnerId
      targetRuntime = expected.runtime
      expectedBinding = expected
      candidateBinding = null
      bindingAtEffect = expected
      ownerFence and claimGeneration both incremented
    persist the complete operation fence before Runtime.resolve
```

The first claim, lease takeover, phase change, candidate record, and operation result are each persisted
with the fence. `deadline` is a fixed upper bound. Infinite polling, lease renewal, or client retry must not
extend it. After a process or Server restart, Session activation and the durable handler read the persisted
operation, ownerFence, claimGeneration, phase, candidateKey, and expectedBinding. Before the deadline, they
can continue only with the current tuple. When the owner lease expired, they first take over and then
continue. A retry from the old owner fails closed on its stale fence even when it carries the same
operationId. At the deadline, first reconcile the candidate and current Binding. The operation can become
`succeeded` when there is no candidate or adoption is confirmed, and `failed` when inability to adopt is
confirmed. When the candidate still exists but the cleanup result is uncertain, the original operation
becomes terminal with `outcome=blocked` and `phase=cleanup-pending`. This terminates only the original
operation. It does not permanently occupy Session Binding admission. An independent, bounded cleanup fence
takes over candidate cleanup. A new Binding operation can start when the current Binding is not that
candidate.

### Trigger conditions

All of these conditions must hold:

1. `session.admission == ready` and current-generation Activity is `idle`;
2. execution is on the current Binding's `runnerId`, and the Runtime and working directory still match;
3. the Runtime adapter on that Runner confirms with deterministic evidence that `runtimeSessionId` no
   longer exists;
4. this Input has not been written to the Transcript or submitted to the Runtime;
5. the expected Binding seen by the Server remains current during both Binding replacement and the later
   Input record write;
6. the sole owner has durably claimed the recovery operation fence before candidate creation.

Runtime unavailability, timeout, permission failure, incompatible response, data corruption, or any result
that cannot distinguish "temporarily unreadable" from "definitely absent" does not satisfy these
conditions. A request reaching another Runner is also not missing. It must route back to the Runner that
owns the Binding or fail explicitly. Missing recovery must not be used to migrate to another Runner.

### Resolution and replacement order

```text
expected = AgentSession.currentBinding
fence = Session.beginRecovery(expected, operationId, ownerId, deadline)
resolveToken = fenceToken(fence, expectedBinding = expected, candidateBinding = null,
                           bindingAtEffect = expected)
resolved = runFenced(resolveToken,
  token => Runtime.resolve(expected, fenceToken = token),
  result => Session.writePhaseIfFenceMatches(resolveToken, result))

if resolved is ready:
    fence = Session.reloadCurrentFence(fence.operationId)
    require fence.expectedBinding == expected
    require fence.candidateBinding == null
    require Session.currentBinding == expected
    persistReadyPhaseIfFenceMatches(fence,
      bindingAtEffect = expected, result = ready)
    postBindingFence = Session.reloadCurrentFence(fence.operationId)
    require postBindingFence.bindingAtEffect == expected
    selected = expected
else if resolved is definitely-missing and fence is owned:
    createToken = fenceToken(
      fence,
      expectedBinding = expected,
      candidateBinding = fence.candidateBinding,
      bindingAtEffect = expected)
    candidate = runFenced(createToken,
      token => Runtime.createOrGetEmpty(
        workDir = AgentSession.workDir,
        candidateKey = fence.candidateKey,
        fenceToken = token),
      result => Session.recordCandidateIfFenceMatches(createToken, result))
    fence = Session.reloadCurrentFence(fence.operationId)
    if create response is lost:
      getFence = Session.reloadCurrentFence(fence.operationId)
      getToken = fenceToken(getFence, expectedBinding = expected,
                            candidateBinding = null,
                            bindingAtEffect = expected)
      candidate = runFenced(getToken,
        token => Runtime.getByKey(getFence.candidateKey, fenceToken = token),
        result => Session.recordCandidateIfFenceMatches(getToken, result))
      if response is still unknown:
        reconciliation = reconcileBindingOperation(fence)
        if reconciliation is unknown or reconciliation is cleanup-pending:
          return recovery_observation_unknown
    fence = Session.reloadCurrentFence(fence.operationId)
    require candidate is ready
    require fence.candidateState == created
    require fence.candidateBinding is complete
    candidateToken = fenceToken(fence, expectedBinding = expected,
                                candidateBinding = fence.candidateBinding,
                                bindingAtEffect = expected)
    cas = compareAndSwapBinding(candidateToken, boundaryKind = missing-recovery)
    selected = cas.currentBinding
    postBindingFence = cas.postFence
    if selected is CAS failure:
      beginCleanupFence(fence, candidate)
      return recovery_cas_conflict
else:
    fail without changing the binding
    return recovery_observation_unknown

inputFence = Session.reloadCurrentFence(fence.operationId)
inputToken = fenceToken(inputFence, expectedBinding = expected,
                        candidateBinding = selected if selected != expected else null,
                        bindingAtEffect = selected)
Session.recordInputIfFenceMatches(inputToken, input = input)
# The protected write advances the operation/session revision; the submit token
# is built from that new fence revision.
submitFence = Session.reloadCurrentFence(fence.operationId)
submitToken = fenceToken(
  submitFence,
  expectedBinding = expected,
  candidateBinding = selected if selected != expected else null,
  bindingAtEffect = selected)
submit = runFenced(submitToken,
  token => Runtime.submitInputExactlyOnce(binding = selected, input = input,
                                          fenceToken = token),
  result => Session.recordSubmitIfFenceMatches(submitToken, result))
completeFence = Session.reloadCurrentFence(fence.operationId)
completeToken = fenceToken(completeFence, expectedBinding = expected,
                           candidateBinding = selected if selected != expected else null,
                           bindingAtEffect = selected)
complete = runFenced(completeToken,
  token => no_external_call(),
  result => Session.completeOperationIfFenceMatches(completeToken, result))
```

```text
persistReadyPhaseIfFenceMatches(fence, bindingAtEffect, result):
  atomically:
    operation = Session.operation(fence.operationId)
    require full fenceMatch(session, operation, fence, serverNow)
    require session.currentBinding == fence.expectedBinding
    require bindingAtEffect == fence.expectedBinding
    newRevision = nextRevision()
    persist operation.phase = resolving, operation.outcome = pending,
      operation.bindingAtEffect = bindingAtEffect,
      operation.revision = newRevision, session.revision = newRevision
  return Session.reloadCurrentFence(fence.operationId)
```

`persistReadyPhaseIfFenceMatches` is an atomic Session write: it requires the current binding to
equal `expected`, writes the resolved `bindingAtEffect=expected` and ready phase, increments the
operation/Session revision, and returns only after `Session.reloadCurrentFence(operationId)`. If
that phase write or the later input write observes a revision change, the caller reloads and builds
a new token before `recordInput`, `submitInputExactlyOnce` or `complete`; no ready path references
an undefined or pre-write `postBindingFence`.

Create/get for the same `candidateKey` must return the same candidate or fail explicitly. When the create
response is lost, query by key. Do not create a second candidate. If Binding CAS fails because of Reset,
handoff, or another committed change, the candidate becomes `orphan`. The Server does not adopt it and
transfers cleanup to an independent cleanup fence. Each create/get first performs a conditional Server
check with the original operation's `fenceMatch` and persists `candidateBinding` with it. A candidate key
alone must not authorize an operation.

```text
recordCandidate(operation, candidate):
  token = Server.recheckBeforeExternalEffect(
    fenceToken(operation, expectedBinding = operation.expectedBinding,
               candidateBinding = null,
                           bindingAtEffect = operation.expectedBinding))
  atomically:
    Server.recheckBeforePersistingEffectResult(token)
    require fenceMatch(session, operation, token, serverNow)
    require operation.candidateBinding == null
    require candidate.key == operation.candidateKey
    require candidate is ready
    require candidate.binding is complete
    persist candidateBinding = candidate.binding with
      bindingEpoch = operation.expectedBinding.bindingEpoch + 1
    persist candidateState = created
    persist phase = candidate-created and advance revision
  return Session.reloadCurrentFence(operation.operationId)

reconcileBindingOperation(operation):
  if operation.candidateBinding != null
     and candidateIsCurrent(operation, {
       key = operation.candidateKey, binding = operation.candidateBinding
     }, session):
    currentFence = Session.reloadCurrentFence(operation.operationId)
    postToken = fenceToken(currentFence,
      expectedBinding = operation.expectedBinding,
      candidateBinding = operation.candidateBinding,
      bindingAtEffect = operation.candidateBinding)
    return persistAdoptedAndCompleteIfFenceMatches(postToken)
  if currentBinding is not operation.expectedBinding:
    beginCleanupFence(operation, candidate = operation.candidateBinding or unknown)
    return orphaned
  if operation.deadline is expired:
    beginCleanupFence(operation, candidate = operation.candidateBinding or unknown)
    return expired
  if operation.candidateBinding == null:
    getToken = fenceToken(operation, expectedBinding = operation.expectedBinding,
                          candidateBinding = null,
                          bindingAtEffect = currentBinding)
    candidate = runFenced(getToken,
      token => Runtime.getByKey(operation.candidateKey, fenceToken = token),
      result => recordCandidateIfFenceMatches(getToken, result))
    if candidate is definitely absent: mark failed with nextAction = retry_binding_operation
    if response is unknown: beginCleanupFence(operation, candidate = unknown)
    operation = Session.reloadCurrentOperation(operation.operationId)
  if operation.candidateBinding exists:
    casToken = fenceToken(operation, expectedBinding = operation.expectedBinding,
                          candidateBinding = operation.candidateBinding,
                          bindingAtEffect = operation.expectedBinding)
    cas = compareAndSwapBinding(casToken, boundaryKind = operation.kind)
    if cas succeeds:
      postFence = cas.postFence
      currentFence = Session.reloadCurrentFence(postFence.operationId)
      require currentFence.revision >= postFence.revision
      require currentFence.candidateBinding == postFence.candidateBinding
      require Session.currentBinding == postFence.candidateBinding
      postToken = fenceToken(currentFence,
        expectedBinding = operation.expectedBinding,
        candidateBinding = postFence.candidateBinding,
        bindingAtEffect = postFence.candidateBinding)
      return persistAdoptedAndCompleteIfFenceMatches(postToken)
    else:
      beginCleanupFence(operation, candidate = operation.candidateBinding)

beginCleanupFence(operation, candidate):
  atomically:
    if candidate is known and candidate.binding is complete
       and candidate.key == operation.candidateKey
       and currentBinding == candidate.binding:
      newRevision = nextRevision()
      mark operation succeeded, candidateState = adopted, phase = completed,
        operation.revision = newRevision, session.revision = newRevision
      return already_adopted
    cleanupId = stableCleanupId(operation.operationId, candidate.key or unknown)
    persist a distinct cleanup operation using the canonical FenceToken with:
      operationId = cleanupId
      sessionId = operation.sessionId
      expectedBinding = operation.expectedBinding
      candidateBinding = candidate.binding or operation.candidateBinding or null
      bindingAtEffect = currentBinding observed at handoff
      ownerId, ownerFence, claimGeneration, revision, leaseUntil
      deadline = min(serverNow + cleanupBudget, operation.deadline + grace)
      candidateKey = candidate.key or operation.candidateKey
      candidateState = orphan or cleanup-pending
    newRevision = nextRevision()
    mark operation outcome = blocked, phase = cleanup-pending,
      candidateState = orphan or cleanup-pending,
      nextAction = retry_candidate_cleanup or operator_reconcile_candidate,
      operation.revision = newRevision, session.revision = newRevision
    release the operation's Session admission slot

persistAdoptedAndCompleteIfFenceMatches(postToken):
  atomically:
    operation = Session.operation(postToken.operationId)
    require fenceMatch(session, operation, postToken, serverNow)
    require postToken.bindingAtEffect == postToken.candidateBinding
    require postToken.candidateBinding != null
    require session.currentBinding == postToken.candidateBinding
    newRevision = nextRevision()
    persist operation.phase = completed, operation.outcome = succeeded,
      operation.candidateState = adopted, operation.revision = newRevision,
      session.revision = newRevision
    return adopted
```

The cleanup fence has its own `operationId`, owner and monotonic `ownerFence`/`claimGeneration`;
it is not a renewal of the expired operation. Its attempts and cleanup `deadline` are durable and
bounded. A binding CAS change always starts or takes over this independent fence with the newly
observed `bindingAtEffect`; it never mutates the original fence to make the old owner fit.
It may be claimed after a process restart or lease takeover, but it cannot be renewed past its
fixed deadline. `candidateState=orphan` means the candidate was found and was never adopted;
`candidateState=cleanup-pending` means the cleanup result is not yet authoritative.

Cleanup calls use the independent fence and compare the candidate identity, not only the key:

```text
cleanupCandidate(cleanupFence):
  cleanupFence = Session.reloadCurrentFence(cleanupFence.operationId)
  require cleanupFence.operationId is the cleanup operation identity
  if now >= cleanupFence.deadline:
    return persistCleanupDeadlineIfRecordMatches(cleanupFence,
      nextAction = operator_reconcile_candidate)
  if Session.currentBinding != cleanupFence.bindingAtEffect
     and Session.currentBinding != cleanupFence.candidateBinding:
    return beginReplacementCleanupFence(cleanupFence,
      reason = cleanup_binding_changed)
  attemptToken = fenceToken(cleanupFence, bindingAtEffect = currentBinding)
  attemptToken = Server.recheckBeforeExternalEffect(attemptToken)
  cleanupFence = persistCleanupAttemptIfFenceMatches(attemptToken)
  # The attempt write increments revision; never reuse attemptToken after this point.
  cleanupFence = Session.reloadCurrentFence(cleanupFence.operationId)
  if now >= cleanupFence.deadline:
    return persistCleanupDeadlineIfRecordMatches(cleanupFence,
      nextAction = operator_reconcile_candidate)

  if cleanupFence.candidateBinding != null
     and Session.currentBinding == cleanupFence.candidateBinding:
    return persistCleanupAdoptedIfFenceMatches(
      fenceToken(cleanupFence, bindingAtEffect = Session.currentBinding))

  if Session.currentBinding != cleanupFence.bindingAtEffect
     and Session.currentBinding != cleanupFence.candidateBinding:
    return beginReplacementCleanupFence(cleanupFence,
      reason = cleanup_binding_changed)

  if cleanupFence.candidateBinding == null:
    getToken = fenceToken(cleanupFence, bindingAtEffect = currentBinding)
    candidate = runFenced(getToken,
      token => Runtime.getByKey(cleanupFence.candidateKey, fenceToken = token),
      result => persistCandidateIdentityIfFenceMatches(getToken, result))
    cleanupFence = Session.reloadCurrentFence(cleanupFence.operationId)
    if candidate is definitely absent:
      return persistCleanupDiscardedIfFenceMatches(
        fenceToken(cleanupFence, bindingAtEffect = currentBinding))
    if candidate is unknown or candidate.binding is not complete:
      return persistCleanupPendingIfFenceMatches(
        fenceToken(cleanupFence, bindingAtEffect = currentBinding),
        reason = candidate_identity_unknown, nextAction = retry_candidate_cleanup)
    cleanupFence = Session.reloadCurrentFence(cleanupFence.operationId)

  # Candidate identity/status write above also advances revision. Build a fresh token for discard.
  discardToken = fenceToken(cleanupFence, bindingAtEffect = currentBinding)
  discarded = runFenced(discardToken,
    token => Runtime.discardCandidate(cleanupFence.candidateKey,
                                      candidateBinding = cleanupFence.candidateBinding,
                                      fenceToken = token),
    result => persistCleanupResultIfFenceMatches(discardToken, result))
  cleanupFence = Session.reloadCurrentFence(cleanupFence.operationId)
  if discarded:
    return persistCleanupDiscardedIfFenceMatches(
      fenceToken(cleanupFence, bindingAtEffect = currentBinding))
  if discarded is unknown and now < cleanupFence.deadline
     and cleanupFence.attempts < cleanupFence.maxAttempts:
    return persistCleanupPendingIfFenceMatches(
      fenceToken(cleanupFence, bindingAtEffect = currentBinding),
      reason = cleanup_result_unknown, nextAction = retry_candidate_cleanup)
  return persistCleanupBlockedIfFenceMatches(
    fenceToken(cleanupFence, bindingAtEffect = currentBinding),
    reason = cleanup_result_unknown, nextAction = operator_reconcile_candidate)
```

All cleanup state helpers (`persistCleanupAttemptIfFenceMatches`,
`persistCandidateIdentityIfFenceMatches`, `persistCleanupPendingIfFenceMatches`,
`persistCleanupDiscardedIfFenceMatches` and `persistCleanupBlockedIfFenceMatches`) use this
atomic shape:

```text
cleanupStateWrite(token, state):
  atomically:
    require full fenceMatch(session, cleanupOperation, token, now)
    require cleanupOperation.candidateKey and candidateBinding are the same durable pair
    newRevision = nextRevision()
    persist state and cleanupOperation.revision = newRevision,
      session.revision = newRevision
  return Session.reloadCurrentFence(token.operationId)

persistCleanupDeadlineIfRecordMatches(cleanupOperation, nextAction):
  atomically:
    require cleanupOperation is still the current operation row
    require now >= cleanupOperation.deadline
    newRevision = nextRevision()
    persist cleanupOperation.outcome = blocked,
      cleanupOperation.phase = cleanup-pending,
      cleanupOperation.candidateState = cleanup-pending,
      cleanupOperation.reason = cleanup_deadline,
      cleanupOperation.nextAction = nextAction,
      cleanupOperation.revision = newRevision,
      session.revision = newRevision
  return cleanup_pending

beginReplacementCleanupFence(oldCleanup, reason = cleanup_binding_changed):
  atomically:
    require oldCleanup is the current cleanup operation row
    if now >= oldCleanup.deadline:
      return persistCleanupDeadlineIfRecordMatches(oldCleanup,
        nextAction = operator_reconcile_candidate)
    oldRevision = nextRevision()
    persist oldCleanup.outcome = blocked, oldCleanup.reason = reason,
      oldCleanup.nextAction = retry_candidate_cleanup,
      oldCleanup.revision = oldRevision, session.revision = oldRevision
    create a new bounded cleanup operation with a new operationId,
      ownerFence/claimGeneration/revision, expectedBinding = Session.currentBinding,
      bindingAtEffect = Session.currentBinding, the same candidateKey and complete
      candidateBinding, and deadline = min(now + cleanupBudget, oldCleanup.deadline)
    return the new cleanup fence
```

An expired lease is taken over by incrementing `ownerFence` and `claimGeneration`; a stale owner
fails the first `full fenceMatch` and cannot write cleanup state. A changed binding creates the
replacement cleanup fence above, never repairs the old token. A cleanup deadline closes the cleanup
operation as `blocked/cleanup-pending` with a manual `nextAction`; it does not discard by guessing.

Before both `getByKey` and `discardCandidate`, the cleanup fence rechecks every field of the
canonical token, including owner lease, revision, expected/candidate binding and the observed
current binding. Every attempt, phase, candidate-identity and cleanup-pending write increments
revision; the caller reloads the cleanup operation and constructs a fresh `FenceToken` before the
next Runtime effect. `currentBinding` may have changed since the original CAS: the old cleanup
fails closed and a new bounded cleanup fence is created with the changed binding. If the complete
candidate binding is current, cleanup returns `already_adopted` and never deletes it. An old cleanup
therefore cannot delete an adopted binding or a newer binding that reused the same Runtime session.
A cleanup response loss is reconciled by the same cleanup identity; it is never converted into an
unbounded retry.

Lease takeover increments `ownerFence` and `claimGeneration` before issuing a new token. An old
owner's create, submit, discard, cleanup, phase write, binding CAS or complete therefore fails the
Server recheck and the Runtime/provider token check, even when it reuses the same `operationId`.
Cleanup for an old operation also must not bypass the independent cleanup fence. When a cleanup response is
lost, query with the same cleanup identity. Do not only write a log or leave the candidate silently.

After `replaceBinding` adopts a candidate, the Server sets the phase to `rebound` and writes the complete
current-Binding tuple, including the new `bindingEpoch`, to `candidateBinding` in the same transaction. From
then on, even if cleanup by an old owner can still locate the old candidate in the Runtime, it must use the
complete `(candidateKey, candidateBinding)` `candidateIsCurrent` predicate to confirm that the current
Binding equals that candidate. Cleanup can only return `already_adopted`. It must not discard, delete, or
reclaim the current Binding. A candidate that was not adopted can be discarded only in `cleanup-pending`
under its matching independent cleanup fence.

After `replaceBinding`, the fence's `expectedBinding` remains the fixed old tuple from the start of the
operation. It is not rewritten to the new Binding. Every later phase, Input record, result archive, or
`complete` must carry both that `expectedBinding` and the adopted `candidateBinding`, and require the current
Binding to equal the candidate. This preserves the CAS condition for the old Binding without incorrectly
classifying the new Binding as stale. Discard/cleanup of a candidate that was not adopted instead requires
the current Binding to still differ from that candidate.

Runner reports only Runtime resolve / create / get / discard facts. The Server decides `replaceBinding` and
`recordInput`. Each command/event carries the complete runner/runtime/session/epoch tuple. Every phase
write, candidate write, Binding CAS, Input write, and complete compares operationId, ownerId, ownerFence,
claimGeneration, expected Binding, and lease. This prevents stale recovery from overwriting Reset, Runtime
change, or another recovery. Runner can submit to the Runtime only after Input persistence succeeds.

### Operation boundaries

| Operation | Replace automatically after confirmed missing | Reason |
|---|---:|---|
| TaskRun or AgentJob submits new Input | Yes | The Input is not yet submitted and can continue in an empty context |
| Follow-up while AgentSession is idle | Yes | It starts a new execution through the same submission order |
| Follow-up during execution | No | The Input targets the current physical execution; replacement would change its meaning |
| Compact | No | A missing context cannot be compacted |
| Cancel | No | A new physical Session is not the original execution target |
| Reset | Not automatic recovery | Ordinary Reset requires `admission=ready`; Unknown permits only explicit force-reset |

Automatic recovery does not replay messages, Prompts, or tool calls from the Mohist Transcript. Transcript
is an audit and presentation record, not a command source for rebuilding Runtime context.

## Runtime change and Reset

Runtime change, ordinary Reset, handoff, and rebind execute only when the current generation has
`activity=idle` and `admission=ready`, and each holds an independent `SessionOperationFence`. All are
rejected while the current generation is `active`, `outcome_pending`, or `unknown`. Active must wait for the
Turn to become terminal. Unknown must first be queried or explicitly force-reset. Handoff/rebind must not
bypass a pending side effect. A superseded unknown state from an old generation enters only
`unresolvedPrevious`; it does not block safe admission in the new generation.

Before any Runtime/provider effect, this fence must persist the fields required for canonical
[`SessionOperationRead`](conventions.md#canonical-sessionoperationread), plus `SessionId`, `candidateKey`,
`targetRunnerId`, and `targetRuntime`. The canonical schema defines the null/required semantics of
`expectedBinding`, `candidateBinding`, target, and candidate key. Every phase write, candidate write, CAS,
and complete uses the same `fenceMatch` and external-effect recheck as recovery. Runner/provider must carry
the same token before it creates a candidate:

```text
BeginBindingOperation(session, kind, operationId, ownerId, expectedRevision,
                      requestedRunnerId, requestedRuntime, deadline):
  requestFingerprint = canonicalFingerprint(
    sessionId = session.id, kind, expectedRevision,
    expectedContextGeneration = session.ContextGeneration,
    expectedBinding = session.currentBinding, requestedRunnerId, requestedRuntime, deadline)
  atomically:
    existing = Session.operation(operationId)
    if existing != null:
      require existing.kind == kind
      require existing.requestFingerprint == requestFingerprint
      return fullSessionOperationRead(existing)
    require session.revision == expectedRevision
    require session.admission == ready
    expected = session.currentBinding
    require expected != null
    require kind in {reset, recovery, force-reset, handoff, rebind}
    require targetRule(kind, expected, requestedRunnerId, requestedRuntime)
    candidateKey = stableCandidateKey(operationId, kind)
    fence = create ActiveOperation(
      operationId = operationId,
      kind = kind,
      requestFingerprint = requestFingerprint,
      expectedRevision = expectedRevision,
      expectedContextGeneration = session.ContextGeneration,
      ContextGeneration = session.ContextGeneration,
      ownerId = ownerId,
      ownerFence = nextOwnerFence(),
      claimGeneration = nextClaimGeneration(),
      revision = nextRevision(),
      expectedBinding = expected,
      candidateBinding = null,
      bindingAtEffect = expected,
      candidateKey = candidateKey,
      targetRunnerId = requestedRunnerId,
      targetRuntime = requestedRuntime,
      candidateState = none,
      leaseUntil = leaseFor(deadline),
      deadline = deadline,
      phase = claimed,
      outcome = pending)

  persist fence before calling Runtime/provider

candidateToken = fenceToken(fence, expectedBinding = expected,
                            candidateBinding = null,
                            bindingAtEffect = expected)
candidateResult = runFenced(candidateToken,
  token => Runtime.createOrGetEmpty(
    workDir = session.workDir,
    candidateKey = fence.candidateKey,
    fenceToken = token),
  result => Session.recordCandidateAttemptOnlyIfFenceMatches(candidateToken, result))
fence = Session.reloadCurrentFence(fence.operationId)
if candidateResult == response_lost:
  getToken = fenceToken(fence, expectedBinding = expected,
                        candidateBinding = null, bindingAtEffect = expected)
  candidateResult = runFenced(getToken,
    token => Runtime.getByKey(fence.candidateKey, fenceToken = token),
    result => Session.recordCandidateAttemptOnlyIfFenceMatches(getToken, result))
  fence = Session.reloadCurrentFence(fence.operationId)
if candidateResult == response_lost or candidateResult == unknown:
  persist operation phase = resolving, outcome = unknown, candidateState = unknown,
    candidateBinding = null, admission = blocked,
    reason = candidate_unknown, nextAction = query_same_candidate_or_manual,
    revision = nextRevision(), session.revision = revision
  return unknown
if candidateResult == definitely-absent or candidateResult == definitely-rejected
   or candidateResult == candidate_not_ready:
  persist operation phase = failed, outcome = failed, candidateState = orphan,
    reason = candidate_not_ready, nextAction = retry_binding_operation,
    revision = nextRevision(), session.revision = revision
  return failed
candidate = candidateResult.candidate
require candidate.key == fence.candidateKey
require candidate.binding is complete
require candidate.binding.runnerId == fence.targetRunnerId
require candidate.binding.runtime == fence.targetRuntime
require candidate.binding.bindingEpoch == fence.expectedBinding.bindingEpoch + 1
fence = recordValidatedCandidateIfFenceMatches(fence, candidate)
require fence.candidateState == created
require fence.candidateBinding is complete
candidateToken = fenceToken(fence, expectedBinding = expected,
                            candidateBinding = fence.candidateBinding,
                            bindingAtEffect = expected)
replace = compareAndSwapBinding(candidateToken, boundaryKind = kind)
postFence = replace.postFence
```

`targetRule` is checked and persisted before the external call: `reset` keeps the current
runner/runtime, `recovery` keeps the current runner/runtime after a confirmed missing result,
`rebind` keeps the runner but may change runtime, and `handoff` changes runner only through the
explicit target. `candidateKey = stableCandidateKey(operationId, kind)` is unique to that
operation. A create response loss is reconciled with `Runtime.getByKey(candidateKey)` and
`recordCandidate`; it never calls create with a new key.

The final step of `replaceBinding` compares the complete `expectedBinding`, current owner
lease/generation, `candidateBinding`, revision, and operationId again in the same Session transaction. Only
success increments `bindingEpoch` and `ContextGeneration` and persists
`ContextBoundary.Kind = handoff | rebind` and `session.context_reset`. If new Session creation fails, CAS
conflicts, or the provider response is lost, retain the original Binding and make the operation `unknown` or
explicitly failed. The caller can only query or retry with the same `operationId`; it must not implicitly
create again. A query that finds a successful operation must return the original `ContextGeneration`,
boundary, bindingEpoch, and result. It must not increment again or create another physical Session.

Compact uses the same complete fence even though it does not replace the Binding:

```text
BeginCompact(session, operationId, ownerId, expectedRevision, deadline):
  requestFingerprint = canonicalFingerprint(
    sessionId = session.id, kind = compact, expectedRevision,
    expectedContextGeneration = session.ContextGeneration,
    expectedBinding = session.currentBinding, deadline)
  atomically:
    existing = Session.operation(operationId)
    if existing != null:
      require existing.kind == compact
      require existing.requestFingerprint == requestFingerprint
      return fullSessionOperationRead(existing)
    require session.revision == expectedRevision
    require session.admission == ready
    expected = session.currentBinding
    fence = create ActiveOperation(kind = compact,
      sessionId = session.id, operationId, ownerId,
      requestFingerprint = requestFingerprint,
      expectedRevision = expectedRevision,
      expectedContextGeneration = session.ContextGeneration,
      ownerFence = nextOwnerFence(), claimGeneration = nextClaimGeneration(),
      revision = nextRevision(), expectedBinding = expected, candidateBinding = null,
      bindingAtEffect = expected,
      leaseUntil = leaseFor(deadline), deadline, contextGeneration = session.ContextGeneration,
      phase = claimed, outcome = pending)
    persist fence before Runtime.compact

token = fenceToken(fence, expectedBinding = expected, candidateBinding = null,
                   bindingAtEffect = expected)
result = runFenced(token,
  t => Runtime.compact(binding = expected, fenceToken = t),
  r => Session.persistCompactBoundaryIfFenceMatches(
         token, contextGeneration = fence.contextGeneration, result = r))
```

Compact completion keeps the same binding and `ContextGeneration`; a stale or lost result remains
the same operation's `unknown`/queryable outcome. It must not be treated as a successful boundary
or retried with a new operationId.

Reset does not change the Runtime. A Runtime-change `rebind` can change `runtime`, but it cannot change
`runnerId`. RunnerId can change only through an explicit durable `handoff` operation. It must not be
inferred from recovery, Runner reconnect, or an old event. Neither operation can change the AgentSession
working directory. Both preserve the existing Transcript. They do not migrate or replay Runtime context or
establish physical Session history. Ordinary Reset, Runtime change, and confirmed missing recovery each
start a new `ContextGeneration`. Ordinary Compact is the only Context boundary that does not increment
`ContextGeneration`.

`SessionOperationFence.Kind` has these fixed meanings:

| Kind | Allowed Binding change | Purpose |
|---|---|---|
| `recovery` | Rebuild a Runtime Session after confirmed missing on the same Runner only | Must not migrate Runner |
| `rebind` | Explicit Runtime/Binding replacement on the same Runner | Must not change `runnerId` or treat an unknown fact as missing |
| `handoff` | Move from the old Runner to an explicitly specified new Runner | Only an explicit handoff operation can start it; Runner restart or an event must not imply it |
| `stop` | Do not replace Binding; stop a specified Turn | A durable operation fence protects queued cancel or Runtime.stop and does not treat unknown as cancelled |

`handoff` and same-Runner missing recovery are separate paths. The handoff fence must record the complete
`candidateBinding`, target Runner, CAS `expectedBinding`, owner, ownerFence, claimGeneration, phase,
deadline, and lease. `runnerId` can change only after that operation completes. Each command/event from the
old Runner must carry the complete `(runnerId, runtime, runtimeSessionId, bindingEpoch)` tuple and the
applicable `operationId` and `claimGeneration`. When any field does not match the current Binding or
operation fence, it fails closed. It must not update Turn, Activity, Transcript, or clean up the new Binding.

The default policy retains the original Turn's `unknown` fact. It does not replay automatically or change
the fact to success, failure, or `idle`. The Server can establish a new Context boundary only after the user
explicitly acknowledges the risk and provides a new `force-reset` operationId. Force-reset is a
risk-confirmed supersede/takeover operation, not an ordinary Reset. It has its own `operationId`, `ownerId`,
`ownerFence`, `claimGeneration`, `deadline`, lease, phase, `expectedBinding`, and `candidateBinding`. It also
records the superseded old operation ID when one exists. The old provider operation remains `unknown`. The
Server does not rewrite it as successful, failed, or stopped and does not fabricate that the old Binding
disappeared.

Force-reset can be claimed only when all of these conditions hold:

1. Canonical Activity or an ActiveOperation targeting the current `ContextGeneration` is actually
   `unknown`, and that pending fact blocks ordinary admission. `unresolvedPrevious` from an old generation
   alone is not sufficient for another force-reset.
2. The caller provides a new force-reset operationId, explicit risk confirmation, and acknowledgement that
   failure or duplicate side effects can still occur.
3. The request carries the current Session revision and the Context generation observed in the same read.
   If the revision or current Binding changed, reject and require another read. Do not overwrite a concurrent
   operation.
4. The Server can retain the old Input/Turn, old operation result, and old Binding tuple while selecting a
   new Context/Binding for the new operation. The old Input/Turn is not rewritten or replayed.

Force-reset does not call ordinary `BeginRecovery`. One collector first collects pending facts, then one
atomic supersede/takeover runs. The collector is the only entry point. It covers unknown `SessionInput`,
`AgentTurn`, dispatch attempt, Runtime side effect, and `ActiveOperation`, if present, from the current
generation:

```text
collectUnresolvedTargets(session, generation):
  candidates = []
  if session.ActiveOperation != null
     and session.ActiveOperation.ContextGeneration == generation
     and session.ActiveOperation.outcome == unknown:
    candidates += UnresolvedTargetRead(
      targetKind = operation,
      targetId = session.ActiveOperation.operationId,
      requestId = null,
      contextGeneration = generation,
      originalOperationId = session.ActiveOperation.operationId,
      expectedBinding = session.ActiveOperation.expectedBinding,
      nextAction = inspect_same_operation,
      supersededByOperationId = null,
      outcome = unknown)
  for request in session.requestMaps where request.ContextGeneration == generation
                              and request.State == unknown:
    inputId = request.InputId or stableUnknownInputTargetId(session.id, request.RequestId)
    candidates += target(
      targetKind = input, targetId = inputId,
      requestId = request.RequestId,
      originalOperationId = operationOriginForInput(inputId) or null,
      contextGeneration = generation,
      expectedBinding = bindingObservedForInput(inputId) or null,
      nextAction = inspect_input, outcome = unknown)
  for turn in session.turns where turn.ContextGeneration == generation
                        and turn.status == unknown:
    primaryInput = firstInputForTurn(turn.turnId)
    candidates += target(
      targetKind = turn, targetId = turn.turnId,
      requestId = primaryInput?.requestId or null,
      originalOperationId = operationOriginForInput(primaryInput?.inputId) or null,
      contextGeneration = generation,
      expectedBinding = bindingObservedForTurn(turn.turnId) or null,
      nextAction = inspect_turn, outcome = unknown)
  for attempt in session.dispatchAttempts where attempt.ContextGeneration == generation
                               and attempt.dispatchStatus == unknown:
    input = session.inputForDispatchAttempt(attempt.dispatchAttemptId)
    candidates += target(
      targetKind = dispatch-attempt, targetId = attempt.dispatchAttemptId,
      requestId = input?.requestId or null,
      originalOperationId = operationOriginForInput(input?.inputId) or null,
      contextGeneration = generation,
      expectedBinding = bindingObservedForDispatch(attempt.dispatchAttemptId) or null,
      nextAction = query_same_dispatch_attempt, outcome = unknown)
  for effect in session.runtimeEffects where effect.ContextGeneration == generation
                             and effect.outcome == unknown:
    candidates += target(
      targetKind = runtime-effect, targetId = effect.effectId,
      requestId = effect.requestId or null,
      originalOperationId = effect.originalOperationId or null,
      contextGeneration = generation, expectedBinding = effect.expectedBinding or null,
      nextAction = inspect_runtime_effect, outcome = unknown)
  return deduplicateAndStableSort(candidates, key = (targetKind, targetId))
```

For `input`, `turn` and `dispatch-attempt`, `requestId` is required when the child record has one;
for `operation` it is always null; `runtime-effect` may be null when no caller request was recorded.
`originalOperationId` is null only when the target has no durable originating operation. The
collector resolves the optional origin and binding through the existing durable Input/Turn/dispatch
records; it never invents either value. `targetId` and `originalOperationId` are separate fields.
A target's `expectedBinding` is the complete observed tuple or explicit null.

```text
BeginForceReset(session, newOperationId, ownerId, expectedRevision,
                expectedContextGeneration, expectedBinding, confirmation, deadline):
  fingerprint = canonicalFingerprint(
    sessionId = session.id, kind = force-reset, expectedRevision,
    expectedContextGeneration, expectedBinding,
    confirmation, deadline)
  atomically:
    existing = Session.operation(newOperationId)
    if existing != null:
      require existing.kind == force-reset
      require existing.requestFingerprint == fingerprint
      require existing.expectedRevision == expectedRevision
      require existing.expectedContextGeneration == expectedContextGeneration
      require existing.expectedBinding == expectedBinding
      return fullSessionOperationRead(existing)
    require session.revision == expectedRevision
    require session.ContextGeneration == expectedContextGeneration
    require session.currentBinding == expectedBinding
    targets = collectUnresolvedTargets(session, expectedContextGeneration)
    require targets is not empty
    require every target.outcome == unknown
    require ActiveOperation == null
            || (ActiveOperation.ContextGeneration == expectedContextGeneration
                && ActiveOperation.outcome == unknown)
    require confirmation acknowledges possible old side effects and duplicate work

    for target in targets:
      target.supersededByOperationId = newOperationId
      persist target in Session.unresolvedPrevious
    old = ActiveOperation targeting expectedContextGeneration
    if old != null:
      require targets contains (targetKind = operation, targetId = old.operationId)
      if old.kind == steer:
        settleSteerTargetTerminal(old, Session.reloadCurrentFence(old.operationId),
          cause = target_turn_superseded,
          adapterAttemptStarted = old.phase == steer-dispatching)
        persist old.supersededByOperationId = newOperationId
      else:
        persist old.phase = superseded, old.outcome = unknown,
          old.supersededByOperationId = newOperationId
    create new ActiveOperation with:
      sessionId = session.id
      operationId = newOperationId
      requestFingerprint = fingerprint
      expectedRevision = expectedRevision
      expectedContextGeneration = expectedContextGeneration
      ContextGeneration = expectedContextGeneration
      ownerId = ownerId
      ownerFence = nextOwnerFence()
      claimGeneration = nextClaimGeneration()
      revision = nextRevision()
      expectedBinding = expectedBinding
      candidateBinding = null
      bindingAtEffect = expectedBinding
      candidateKey = stableCandidateKey(newOperationId, force-reset)
      targetRunnerId = expectedBinding.runnerId
      targetRuntime = expectedBinding.runtime
      candidateState = none
      deadline = deadline
      supersededTargets = targets
      admission = blocked
      phase = claimed
      outcome = pending, reason = null, nextAction = create_force_reset_binding
      targetTurnId = null
      observedAt = now
    persist the complete operation fence and ContextBoundary(kind = force-reset,
      result = pending), and persist Session.admission = blocked before any Runtime effect
    persist session.admission = blocked,
      reason = force_reset_in_progress,
      nextAction = query_force_reset_operation
    # admission remains blocked until force-reset binding/context commit
  return the new fence

candidateToken = fenceToken(newFence,
  expectedBinding = newFence.expectedBinding,
  candidateBinding = null,
  bindingAtEffect = newFence.expectedBinding)
createResult = runFenced(candidateToken,
  token => Runtime.createOrGetEmpty(
    workDir = session.workDir,
    candidateKey = newFence.candidateKey,
    fenceToken = token),
  result => Session.recordCandidateAttemptOnlyIfFenceMatches(candidateToken, result))
newFence = Session.reloadCurrentFence(newOperationId)

# Preserve response_lost as a branchable result. It means create may have happened;
# generic unknown means that even the lookup path is not yet classified.
if createResult == response_lost:
  lookupFence = Session.reloadCurrentFence(newOperationId)
  lookupToken = fenceToken(lookupFence,
    expectedBinding = lookupFence.expectedBinding,
    candidateBinding = null,
    bindingAtEffect = lookupFence.expectedBinding)
  lookupResult = runFenced(lookupToken,
    token => Runtime.getByKey(lookupFence.candidateKey, fenceToken = token),
    result => Session.recordCandidateAttemptOnlyIfFenceMatches(lookupToken, result))
  newFence = Session.reloadCurrentFence(newOperationId)
  if lookupResult == response_lost or lookupResult == unknown:
    return persistForceResetCandidateUnknown(newFence,
      reason = force_reset_candidate_unknown,
      nextAction = query_same_candidate_or_manual)
  if lookupResult == definitely-absent:
    return persistForceResetCandidateRetryable(newFence,
      reason = force_reset_candidate_not_found,
      nextAction = retry_same_force_reset_candidate)
  if lookupResult == definitely-rejected:
    return persistForceResetCandidateDefinitelyRejected(newFence)
  if lookupResult == candidate_not_ready:
    return persistForceResetCandidateUnknown(newFence,
      reason = force_reset_candidate_not_ready,
      nextAction = query_same_candidate_or_manual)
  candidate = lookupResult.candidate
else if createResult == unknown:
  return persistForceResetCandidateUnknown(newFence,
    reason = force_reset_candidate_unknown,
    nextAction = query_same_candidate_or_manual)
else if createResult == definitely-rejected:
  return persistForceResetCandidateDefinitelyRejected(newFence)
else if createResult == definitely-absent or createResult == candidate_not_ready:
  return persistForceResetCandidateRetryable(newFence,
    reason = force_reset_candidate_not_ready,
    nextAction = retry_same_force_reset_candidate)
else:
  candidate = createResult.candidate

if not candidateIdentityMatches(newFence, candidate):
  if candidate has a complete provider-confirmed binding:
    return beginCleanupFence(newFence, candidate,
      reason = force_reset_candidate_identity_mismatch,
      nextAction = retry_candidate_cleanup)
  return persistForceResetCandidateUnknown(newFence,
    reason = force_reset_candidate_identity_unknown,
    nextAction = operator_reconcile_candidate)
newFence = recordValidatedCandidateIfFenceMatches(newFence, candidate)

newFence = Session.reloadCurrentFence(newOperationId)
require newFence.candidateState == created
require newFence.candidateBinding is complete
finalizeToken = fenceToken(newFence,
  expectedBinding = newFence.expectedBinding,
  candidateBinding = newFence.candidateBinding,
  bindingAtEffect = newFence.expectedBinding)
cas = compareAndSwapBinding(finalizeToken, boundaryKind = force-reset)
postToken = cas.postFence
finalized = effectWithFence(postToken,
  no_external_call(),
  result => atomically:
    require session.currentBinding == postToken.candidateBinding
    persist ContextBoundary result = succeeded,
      session.context_reset, operation.phase = completed,
      operation.outcome = succeeded, operation.reason = null,
      operation.nextAction = inspect_session,
      session.unresolvedPrevious = the same target array
    clear ActiveOperation
    set session.admission = ready, reason = null, nextAction = inspect_session)
if finalized is not committed:
  beginCleanupFence(newFence, candidate)
```

`Runtime.createOrGetEmpty` and `Runtime.getByKey` preserve the normalized result discriminator
`ready(candidate)`, `candidate_not_ready`, `definitely-absent`, `definitely-rejected`,
`response_lost`, or `unknown`. `recordCandidateAttemptOnlyIfFenceMatches` records the fenced
attempt and raw discriminator but never writes `candidateState=unknown` for `response_lost`; this
is what keeps the lookup branch reachable. `recordValidatedCandidateIfFenceMatches` is the only
write that may set `candidateState=created` and `candidateBinding`; it requires the complete
identity predicate above, current expected binding, and the pre-CAS fence, increments revision,
and returns a reloaded operation. A provider response that lacks a complete binding is not a
candidate for CAS or discard.

```text
persistForceResetCandidateUnknown(fence, reason, nextAction):
  atomically:
    require fence is still the current operation fence
    require no complete validated candidate binding has been recorded
    newRevision = nextRevision()
    persist phase = resolving, outcome = unknown,
      candidateKey = fence.candidateKey, candidateBinding = null,
      candidateState = unknown, admission = blocked,
      reason = reason, nextAction = nextAction,
      revision = newRevision, session.revision = newRevision
  return unknown

persistForceResetCandidateRetryable(fence, reason, nextAction):
  atomically:
    require fence is still the current operation fence
    require Session.currentBinding == fence.expectedBinding
    persist phase = resolving, outcome = pending,
      candidateKey = fence.candidateKey, candidateBinding = null,
      candidateState = none, admission = blocked,
      reason = reason, nextAction = nextAction,
      revision = nextRevision(), session.revision = revision
  return retry_same_force_reset_candidate

persistForceResetCandidateDefinitelyRejected(fence):
  atomically:
    require fence is still the current operation fence
    require fence.candidateBinding == null
    require fence.candidateState in {none, unknown}
    require Session.currentBinding == fence.expectedBinding
    if now < fence.deadline:
      persist phase = resolving, outcome = pending,
        candidateState = none, candidateBinding = null,
        reason = force_reset_candidate_rejected,
        nextAction = retry_same_force_reset_candidate,
        revision = nextRevision(), session.revision = revision
      # Reuse the same candidateKey within the bounded deadline. There is no
      # candidate identity to clean up and no CAS token to build.
      return retry_same_force_reset_candidate
    persist phase = failed, outcome = blocked,
      candidateState = none, candidateBinding = null,
      reason = force_reset_candidate_rejected,
      nextAction = inspect_force_reset_operation,
      revision = nextRevision(), session.revision = revision
    clear any retry signal and keep admission = blocked
    return blocked

candidateIdentityMatches(operation, candidate):
  return candidate.key == operation.candidateKey
    and candidate.binding is complete
    and candidate.binding.runnerId == operation.targetRunnerId
    and candidate.binding.runtime == operation.targetRuntime
    and candidate.binding.bindingEpoch == operation.expectedBinding.bindingEpoch + 1

reconcileForceResetCandidate(operation):
  require operation.operationId is the original force-reset operationId
  if operation.outcome in {succeeded, rejected, failed, blocked}:
    return fullSessionOperationRead(operation)
  if operation.candidateBinding != null
     and candidateIsCurrent(operation,
       { key = operation.candidateKey, binding = operation.candidateBinding }, Session):
    currentFence = Session.reloadCurrentFence(operation.operationId)
    postToken = fenceToken(currentFence,
      expectedBinding = operation.expectedBinding,
      candidateBinding = operation.candidateBinding,
      bindingAtEffect = operation.candidateBinding)
    return persistAdoptedAndCompleteIfFenceMatches(postToken)
  if Session.currentBinding != operation.expectedBinding:
    if operation.candidateBinding is complete:
      return beginCleanupFence(operation, candidate = operation.candidateBinding,
        reason = force_reset_binding_changed,
        nextAction = retry_candidate_cleanup)
    return persistForceResetCandidateUnknown(operation,
      reason = force_reset_binding_changed,
      nextAction = operator_reconcile_candidate)
  if now >= operation.deadline:
    if operation.candidateBinding is complete:
      return beginCleanupFence(operation, candidate = operation.candidateBinding,
        reason = force_reset_deadline,
        nextAction = retry_candidate_cleanup)
    return persistForceResetCandidateUnknown(operation,
      reason = force_reset_candidate_unknown,
      nextAction = operator_reconcile_candidate)
  if operation.candidateState in {none, unknown}:
    queryFence = Session.reloadCurrentFence(operation.operationId)
    queryToken = fenceToken(queryFence,
      expectedBinding = queryFence.expectedBinding,
      candidateBinding = null,
      bindingAtEffect = queryFence.expectedBinding)
    lookup = runFenced(queryToken,
      token => Runtime.getByKey(queryFence.candidateKey, fenceToken = token),
      result => Session.recordCandidateAttemptOnlyIfFenceMatches(queryToken, result))
    operation = Session.reloadCurrentOperation(operation.operationId)
    if lookup == response_lost or lookup == unknown:
      return persistForceResetCandidateUnknown(operation,
        reason = force_reset_candidate_unknown,
        nextAction = query_same_candidate_or_manual)
    if lookup == definitely-absent:
      return persistForceResetCandidateRetryable(operation,
        reason = force_reset_candidate_not_found,
        nextAction = retry_same_force_reset_candidate)
    if lookup == definitely-rejected:
      return persistForceResetCandidateDefinitelyRejected(operation)
    if lookup == candidate_not_ready:
      return persistForceResetCandidateUnknown(operation,
        reason = force_reset_candidate_not_ready,
        nextAction = query_same_candidate_or_manual)
    candidate = lookup.candidate
    if not candidateIdentityMatches(operation, candidate):
      if candidate has a complete provider-confirmed binding:
        return beginCleanupFence(operation, candidate,
          reason = force_reset_candidate_identity_mismatch,
          nextAction = retry_candidate_cleanup)
      return persistForceResetCandidateUnknown(operation,
        reason = force_reset_candidate_identity_unknown,
        nextAction = operator_reconcile_candidate)
    operation = recordValidatedCandidateIfFenceMatches(operation, candidate)
  require operation.candidateState == created
  require operation.candidateBinding is complete
  # Only this explicit ready/created path may construct a CAS token.
  return continueForceResetWithCandidate(operation)
```

`persistForceResetCandidateRetryable` leaves the operation pending with the same `candidateKey`;
the durable handler re-enters `Runtime.createOrGetEmpty` with that key before any fresh `getByKey`
probe. A retry response loss again enters the same response-loss `getByKey` branch, an explicit
ready candidate repeats identity validation, and a second absent result remains pending until the
fixed deadline. A `definitely-rejected` result from the create response-loss lookup and from the
restart reconciliation lookup follows the same `persistForceResetCandidateDefinitelyRejected`
transition: before the deadline, `candidateState=none`, `candidateBinding=null`,
`outcome=pending`, `reason=force_reset_candidate_rejected`, and
`nextAction=retry_same_force_reset_candidate`; at the deadline, `candidateState=none`,
`outcome=blocked`, the same stable reason, and `nextAction=inspect_force_reset_operation`.
Neither branch starts cleanup, reads an unconfirmed binding, or constructs a CAS. A response loss
or generic unknown instead persists `candidateState=unknown`, `outcome=unknown`,
`nextAction=query_same_candidate_or_manual`; only a complete exact candidate may enter the
independent cleanup or CAS paths. No branch creates a new candidate key or CAS token from a
transient response.

When no old `ActiveOperation` exists, `targets` still turns unknown Input/Turn/dispatch/Runtime side effects
into durable `UnresolvedPrevious` and exposes them in `SessionOperationRead.supersededTargets`. "No
ActiveOperation" must not skip the supersede identity. If the old operation is still known pending rather
than unknown, force-reset is rejected. The old operation's `unknown` result,
`supersededByOperationId`, and old Binding tuple remain in its persisted operation record. The new fence can
advance only under its own complete condition tuple. It cannot mark the old operation completed or claim
that the old Binding was stopped or deleted. On finalize failure or response loss, the new operation remains
`pending` / `unknown` with `admission=blocked`. The old operation and all targets remain queryable under
their original identities.

A new Input/Turn can use the current `ContextGeneration` only after the new Binding is selected stably and
the new ContextBoundary commits. On force-reset response loss, query or retry with the same operationId. It
returns the same new Context, bindingEpoch, and mapping. It must not create a second Context. Unknown facts
from the old `ContextGeneration`, late facts from the old Binding, and potential side effects remain
queryable. They are exposed through `unresolvedPrevious`, `unresolvedPreviousCount`,
`currentContextActivity`, `nextAction`, and a risk warning.

## Module ownership

- Workflow owns TaskRun and the Workflow Action contract. It does not interpret the Session Transcript.
- Agent owns Mohist Agent and AgentJob. It does not interpret Session Activity.
- Session owns AgentSession identity, source, workDir, SessionInput, AgentTurn, Activity, current Binding,
  Transcript, context, and usage.
- Runner executes resolved work, creates or recovers a Runtime Session, and reports physical facts.
- The Runtime adapter hides SDK / protocol, cache, process, files, event reconciliation, and error
  classification.
- Web and CLI consume only Activity, current Binding, and Transcript from the Server. They do not derive
  Session state from historical results.

Server is the sole state arbiter for Binding and Activity. Runner cannot decide independently that the
current Binding changed or close an AgentSession because a Runtime process exited.

## Test boundaries

Default tests do not access a real Runtime, network, process, file-system Session, or wall clock. At minimum,
cover these cases:

- One AgentSession reuses its current Binding across a task, retry, Follow-up, and Runner restart.
- SessionInput and AgentTurn identity, route, ownership, and order remain unchanged after process restart.
  Steer does not duplicate a Turn.
- A steer Input commits with its durable operation/effect in the same transaction. Duplicate calls,
  response loss, and Server restart only query or replay the same operation. They must not return accepted
  without a replayable effect.
- Retrying the same Input does not create a duplicate record. Backpressure does not lose an accepted Input.
- Binding replacement and `session.context_reset` persist atomically. The event contains no physical Session
  history.
- `outcome_pending` and `unknown` are not mapped to Turn `idle`. When Activity is idle, no Turn is
  non-terminal.
- `admission=blocked` prevents a new Turn, Compact, Reset, and recovery while a side effect is unknown.
- Queue full rejects before admission. Enqueue failure after accepted retains the queued fact and a durable
  handler retries it.
- Uncertain stop or Input admission becomes `unknown` and is not replayed automatically.
- Reset, Runtime change, and confirmed missing replace the current Binding atomically with an operation
  identity only under `admission=ready`.
- Concurrent recovery has only one owner. Server restart can reconcile, expire, and clean up a candidate.
- Lease-expiry takeover increments ownerFence/claimGeneration. All phase/write/delete/complete attempts by
  the old owner fail closed. Candidate CAS failure does not adopt the candidate and completes idempotent
  cleanup. An old cleanup does not delete an adopted candidate. Create/submit response loss does not replay
  implicitly.
- Runner handoff and same-Runner missing recovery are separate. A stale tuple event is rejected.
- `force-reset` retains the old unknown and can explicitly create a new Input/Turn in a new Context. Risk and
  next action are queryable.
- Launch Session rejection and permanent failure each have durable Job outcome, reason, nextAction,
  null+reason mapping, and tombstone.
- A stale expected Binding and an event from an old Runtime Session are rejected.
- Binding replacement does not create a new AgentSession, store physical Session history, or copy physical
  Session data.
- TaskRun / AgentJob results and AgentSession Activity do not overwrite each other.

## #378 Target spec: Input/Turn lifecycle and Runtime recovery

This section is the target definition of the AgentSession execution contract. It supplements and refines
the AgentSession, Input, Turn, and Binding models above. The public projections defined below take priority
over implementation-internal terms such as `executing`, `completed`, or `failed`. This section does not
introduce a new endpoint, CLI syntax, or event DSL. CLI and Web consume the same canonical Server state.
Workflow can reuse this state later. Clients do not derive the lifecycle independently.

### Object relationships and responsibilities

| Object | Domain responsibility | Relationship to other objects |
|---|---|---|
| Agent | Long-lived reusable named entity that stores definition and config references | One Agent can produce multiple AgentJobs and AgentSessions |
| AgentJob | Owner and result arbiter of one launch work item | References one first Input/Turn; does not own the Follow-up lifecycle |
| AgentSession | Public logical Session that can continue interaction | Stably owns Inputs, Turns, Transcript, context, usage, and current Binding |
| Input | Execution intent submitted once by a user or the system | Has a stable Input ID and exactly one Turn; retry does not create a copy |
| Turn | Queue, execution, observation, and result record for an Input | Has a stable Turn ID; Turns in the same Session are serialized |
| Runtime Session | Physical execution context in Runner/Runtime | Can disappear, be rebuilt, or be rebound; does not change AgentSession identity |
| AgentJob executor | Gives launch intent to Session and Runner | Does not bypass Session records or treat a Runtime event as a public result |

AgentSession is the public logical identity. It is not an alias for Runtime Session or AgentJob. AgentJob
represents only one launch work item. A Follow-up appends to the original AgentSession and does not create an
AgentJob. Agent Instructions, Runtime, Model, Variant, and Skills are resolved and snapshotted at launch.
Persona is not a new domain object created by #378. If #377 later defines it, this design consumes only the
resolved execution snapshot and does not define Persona fields or configuration experience. `target` means
only the target reference in the existing launch context. #378 does not create a target model. Snapshot
changes do not write back to an existing Session. Runtime Session loss or reconstruction, Runner restart,
and rebinding must all map back to the same AgentSession, Input, and Turn records.

Each launch or Follow-up must have an explicit Workspace and target. Both enter the persisted record and
canonical return value. When CLI omits Workspace, the entry point first resolves the actual default scope
for the current Project, then returns and persists that scope. It must not return null, return only
"default," or make Web guess the directory.

### Stable identity and canonical projections

The client must first provide `launchRequestId` and canonical `launchRequestFingerprint`, which are the
idempotency key and complete request-envelope hash for this launch. In the admission transaction, the Server
looks up an existing request by `(projectId, agentId, launchRequestId)`. After response loss, the same key
and fingerprint must return the original launch rejection/operation. A changed payload must return
`rejected(idempotency_key_reused)`. Only a new requestId can create a new launch. Only the first prepare
generates and persists reservations for `launchOperationId`, `AgentJobId`, `AgentSessionId`, `InputId`, and
`TurnId`. Session/Input/Turn IDs become addressable live mappings only after Session accept succeeds. Once a
materialized ID is persisted, queueing, Runner restart, Binding replacement, retry, Compact, Reset, or
force-reset does not change it. RuntimeSessionId identifies only the current physical Binding. It can be
replaced and must not be the identity of the public logical Session. `launchRequestId` permanently maps to
one `launchOperationId`. The client must not use the Server-generated `launchOperationId` as the first
request identity. Duplicate submission finds the original Input/Turn through the same request identity. It
must not create a second Input or side effect.

Server is the authoritative source for the canonical read model and command result. CLI, Web, and other
entry points are adapters. There is no authority direction in which CLI JSON is fixed first and Web then
reuses it. Each returned fact carries at least a Server-produced `revision` and `observedAt`. Revision
increases monotonically when the corresponding authoritative Job/Session state changes. ObservedAt is when
the Server observed that fact. External resume-read cursors and authentication/transport semantics remain in
#387. This issue does not create an endpoint.

| Canonical fact | Question it must answer | Required fields |
|---|---|---|
| AgentJob status | Was this launch work queued, executed, completed, rejected, or failed? | Canonical `AgentJobLaunchRead`, including `launchRequestFingerprint`, status/outcome/reason/nextAction, mapping, `revision`, and `observedAt` |
| Session Activity | Can the current Context of this Session continue, and are old facts still unknown? | Canonical `AgentSessionRead`, including `admission`, reason/nextAction, Activity, Binding, unresolved targets, `revision`, and `observedAt` |
| Input acceptance | Did Mohist durably accept this Input, and which Turn owns it? | Canonical `SessionInputRead`: `sessionId`, `inputId`, `requestId`, `requestFingerprint`, `state`, `acceptanceReason`, `turnId`, `turnRelation`, `steerOperationId`, `steerStatus`, `steerRetryAllowed`, `steerNextAction`, `contextGeneration`, `revision`, `observedAt` |
| Turn result | What are this Turn's execution state, dispatch state, and result? | Canonical `TurnResultRead`, with the sole `outcome`/`reason`/`nextAction` and nullable `result` rules, plus `TurnDispatchRead` |
| Session operation | How does a current or historical Compact/Reset/recovery/handoff/rebind/stop/force-reset/steer converge? | Complete [`SessionOperationRead`](conventions.md#canonical-sessionoperationread); do not copy its field list here |
| Launch context | Which Workspace and existing target did this work actually bind? | `workspace`, `target`, `launchRequestId`, `launchOperationId`, `revision`, `observedAt` |

`unresolvedPrevious` summarizes operations, Inputs, Turns, and side effects that remain unknown in an old
`ContextGeneration`. It must retain operationId, contextGeneration, outcome, and nextAction, and must not be
merged into `currentContextActivity`. Job `sessionId`, `inputId`, and `turnId` are canonical mapping fields.
When absent, they must be `null` and the corresponding `...Reason` must be a non-empty stable value, such as
`session_rejected`, `input_not_accepted`, or `launch_failed_before_session`. A reservation ID must not be
returned as a live mapping. When Session accept durably succeeds, all three mappings materialize together.
There is no half-mapping with only Session or only Input.

Input `state`, `requestId`, unique mapping, and `turnRelation` follow
[`conventions.md#canonical-sessioninput-and-dispatch-schema`](conventions.md#canonical-sessioninput-and-dispatch-schema).
Turn `dispatchStatus` can use only canonical `queued | retrying | blocked | dispatched | unknown |
terminal`. `blocked` is temporarily retryable. Only `terminal` is terminal after reaching an
attempt/deadline limit. `blockedReason` can be non-empty for `blocked` or
`terminal/outcome=blocked`. Unknown dispatch must not be fabricated as blocked or terminal. `nextAction`,
attempt count, attempt identity, and deadline are all required. A client must be able to distinguish
continued coordination, querying the same attempt, and explicit requeue after termination.

`accepted` is an Input admission fact. It does not replace Job status, Session Activity, or Turn result.
`result` is populated only when the state is `terminal` and the Server persisted the final result, subject to
the null rules in conventions. `queued`, `running`, `outcome_pending`, and `unknown` must not fabricate
success, failure, or cancellation. `reason` and `nextAction` use stable, user-understandable semantics and
do not expose a provider event name. CLI/Web can only translate these canonical facts for presentation. They
must not calculate another "completion" from a local log, HTTP state, or Runtime event.

`accepted` is an Input admission fact. `status` is a Turn observation fact. An accepted Input can temporarily
be `queued`, `retrying`, or `blocked`. When the durable dispatch deadline arrives, a definitely-rejected
attempt exists, and the retry budget reaches its boundary, `dispatchStatus=terminal` and Turn
`outcome=blocked`; it must not remain queued forever. Runner unavailability can also make it `unknown`, in
which case `Turn.status=unknown` must be synchronized. A Turn is `running` only after the Runtime confirms
that execution started. Session-level `activity` can still be `idle`, `active`, or `unknown`, but it does not
replace Turn status. `activity=idle` for the current `ContextGeneration` requires no non-terminal Turn and no
incomplete operation. It does not mean that the latest Runtime process is idle and cannot coexist with an
unarchived result.

Turn status is defined as follows:

| Status | Entry condition | Meaning and allowed transitions |
|---|---|---|
| `queued` | Input is accepted and Turn is persisted, but Runtime has not confirmed start. Dispatch can be `queued`, `retrying`, or temporarily `blocked`. | Can become `running`; becomes `terminal/outcome=blocked` when dispatch reaches its limit with a definitely-rejected attempt; becomes `unknown` when a side-effect fact is uncertain |
| `running` | The Runtime at the current complete Binding confirmed that it accepted and executes this Turn | Can become `outcome_pending`, `terminal`, or `unknown` when the result is uncertain |
| `outcome_pending` | Server knows that Input/submit was accepted, but Runtime is not currently executing and the final result is not archived | Can become `terminal` only from an authoritative result or `unknown` when observation loses certainty; must not be treated as success or idle |
| `terminal` | Server persisted irreversible success, failure, cancellation, or a `blocked` result after the dispatch attempt/deadline limit | Terminal; no more automatic replay or result changes |
| `unknown` | Input acceptance, side effect, or execution result cannot be confirmed. When dispatch is unknown, Turn must also be `unknown`. | Non-terminal; only authoritative observation of the same attempt, manual confirmation, or explicit force-reset can handle it; must not redeliver automatically |

Turn has no `idle` state. `activity=idle` holds only when the Session has no non-terminal Turn or incomplete
operation. `outcome_pending` and `unknown` both prevent an ordinary new Turn, Compact, Reset, and recovery.
They differ only in the facts known to Server; they do not change the safety gate. Internal `Executing` maps
to public `running`. Internal Completed/Failed/Cancelled maps to `terminal` only after the result is
persisted. Internal enums must not be exposed directly to CLI/Web.

Canonical AgentJob status is `preparing | queued | running | unknown | terminal`. Terminal `outcome` is
`completed | rejected | failed | cancelled | blocked`. `rejected` means Server confirmed that Session did
not accept this launch. `failed` means an unrecoverable launch/execution failure is confirmed. Both must be
terminal and include stable `reason` and `nextAction`. While acceptance, dispatch, or a Runtime side effect
cannot be confirmed, retain `unknown`; do not fabricate `failed` to clear a wait. Unknown is not queued and
must include a next action such as querying the original operation, manual reconciliation, or force-reset.

### Launch and Follow-up lifecycle

Both call types follow the same logical sequence. They differ only in whether launch creates an AgentJob:

```text
request -> prepare-job -> accept-session -> queue -> execute -> result
```

1. `request` validates the Agent execution-definition snapshot, Workspace, existing target/context, Input
   identity, and current Session. This step does not promise that the Runtime is available. Launch must
   carry caller-generated `launchRequestId` and `launchRequestFingerprint`. They are the only idempotency
   key/envelope hash that the client retains for the first request and every retry.
2. `prepare-job` looks up `(projectId, agentId, launchRequestId)` in AgentJob's own transaction. The same
   fingerprint returns the original Job/operation or rejection tombstone. A changed payload returns
   `idempotency_key_reused` and creates no new reservation. Only a new requestId enters prepare. Only the
   first request generates and persists `launchOperationId`, JobId, internal Session/Input/Turn reservations,
   a fixed launch `deadline`, `launchRequestFingerprint`, `sessionAcceptance=pending`, and one
   `accept-session` durable outbox command. It does not write Session. A reservation is only an idempotent
   placeholder for the coordinator, not an addressable Session, Input, or Turn. Before accept succeeds, Job
   read must return `null + reason` for all three mappings.
3. `accept-session` is an idempotent command carrying `launchOperationId` and reservation IDs, sent by the
   durable launch coordinator. The Session participant transaction writes only its own Session, Input, Turn
   request map, dispatch record, and `SessionLaunchAccepted` / `SessionLaunchRejected` durable event/outbox.
   It cannot update AgentJob in the same transaction. The Session side must make the accept/reject result
   queryable as `pending | accepted | rejected`. Accept materializes the Session/Input/Turn relationship and
   initial dispatch event together. Success returns `accepted=true` and stable InputId/TurnId. The state is
   normally `queued` at this point and must not be written as `running`. Rejected must also persist a stable
   `reason`; it must not exist only in the synchronous response. On definitive rejection, the Session side
   retains an admission tombstone keyed by `launchOperationId` but creates no addressable Session/Input/Turn.
4. `queue` advances the ordered execution queue through the Session event/outbox. Exactly one Turn in the
   same Session can have confirmed execution. A Follow-up queues while the preceding Turn is non-terminal.
   It does not jump the queue, merge, or overwrite. The Session queue limit belongs to this design. Capacity
   claim/release and the view across Sessions belong to `#382`.
5. `execute` has the bound Runner reconcile or recover the Runtime. Turn becomes `running` only after it
   receives a start fact from the current complete Binding. Different AgentSessions can run in parallel.
6. `result` is archived only by the Server from the current Binding, associated InputId/TurnId, and
   authoritative result. A known final result makes the Turn `terminal`. If the Runtime reports idle before
   the result is certain, the Turn remains `outcome_pending`; it must not become `idle`.

Launch creates the first Input/Turn for one new AgentJob. It returns JobId, SessionId, InputId, and TurnId
after the Job coordinator confirms the Session accept event. If Session has not accepted, these three
mapping fields return `null` and their respective reasons, not reservation IDs. AgentJob, AgentSession,
Input, and Turn do not share a cross-aggregate transaction. The AgentJob event/outbox drives an idempotent
Session command. A Session event then drives the Job mapping write-back and first Turn dispatch. A Follow-up
does not create an AgentJob; it only appends an Input/Turn.

`LaunchCoordinator` uses `launchOperationId` as its sole durable identity. It stores only the command
delivery fence, owner/claim lease, deadline, and current phase. It does not copy AgentJob or AgentSession
business facts. After process restart, it scans Jobs with `sessionAcceptance=pending`, claims unexpired work,
and takes over an expired lease after incrementing the owner fence. Every send, query, and Job write-back
reuses the same `launchOperationId`. Retry or response loss first queries the durable Session launch result.
Only when that result is unknown does it redeliver the same accept command. It must not generate a new Job,
Session, Input, Turn, or launch identity.

The launch coordinator must converge to these results:

| Fact | Job result | Mapping and retention |
|---|---|---|
| Session definitively rejects | `terminal / rejected` | `sessionId`, `inputId`, and `turnId` are all null + reason; reservations remain only in the launch tombstone |
| Unrecoverable prepare/outbox/Session failure with confirmed absence of a Session side effect | `terminal / failed` | All non-materialized mappings are null + reason; retain the launch tombstone and no executable outbox |
| Session accepted | `queued` or later `running/terminal` (Job outcome=blocked when first Turn dispatch is terminal blocked) | Return all three live mappings together; later update only status and never replace an ID |
| Response loss or uncertain acceptance/side effect | `unknown` or known current state | First use the original `launchRequestId` to find `launchOperationId`, then query/retry the original operation; do not create a Job, Session, Input, Turn, or submit |

`reason` and `nextAction` are stable canonical Server product semantics, such as `agent_archived`,
`needs_setup`, `invalid_input`, `queue_full`, `session_conflict`, and `launch_persistence_failed`. Temporary
unavailability must continue the same operation and must not become permanent failure early. At the
deadline, the state can become `terminal / failed` only when the Server proved that Session did not accept
and no Runtime side effect is possible. Otherwise, retain `unknown` and provide a next action for manual
reconciliation or force-reset. Do not create a false terminal state.

After response loss or Server restart, a query must first use `launchRequestId` to find the unique
`launchOperationId`, then read the durable acceptance records from Job and Session. Session `accepted`
returns the original three mappings. `rejected` returns the terminal reason. `pending` redelivers the same
accept command. If Session stored the accept record for the same operation but Job write-back failed, the
coordinator writes the same mapping only in the AgentJob transaction. It can retry the same command only
when Session has no record. Therefore, the original launch operation always corresponds to one reservation
set and at most one live mapping set. It produces no dangling live ID or unbounded pending state. The client
does not need to guess or know `launchOperationId` in advance.

### Durable dispatch retry after accepted

After `accept-session` commits Input/Turn, enqueue is not an instantaneous side effect. The same Session
transaction must create `TurnDispatchRead` from
[`conventions.md#canonical-sessioninput-and-dispatch-schema`](conventions.md#canonical-sessioninput-and-dispatch-schema),
fix `dispatchDeadline`, and write durable retry work/outbox. The only key for this retry work is
`dispatchRetryId`. The outbox command, timer, and coordinator claim all use it instead of generating
separate identities. Each retry first atomically claims due work in the Server, increments
`dispatchAttemptCount`, generates one unique `dispatchAttemptId`, and sets status to `retrying`. It then
calls Runner with the complete `FenceToken`. Response loss queries only by the same `dispatchAttemptId` and
does not create a second attempt.

```text
persistInitialDispatch(turn, input, deadline):
  atomically:
    retryId = stableRetryId(turn.id, retrySequence = 0)
    operationId = stableDispatchOperationId(turn.id)
    persist TurnDispatch(
      sessionId = turn.sessionId,
      dispatchStatus = queued, dispatchAttemptCount = 0,
      dispatchAttemptId = null, dispatchDeadline = deadline,
      dispatchOperationId = operationId, dispatchLastResult = none,
      expectedBinding = session.currentBinding, candidateBinding = null,
      dispatchRetryId = retryId, dispatchRetryKind = outbox,
      retryAllowed = true,
      dispatchRetryDueAt = now, dispatchRetryState = pending,
      dispatchRetryOwnerId = null, dispatchRetryClaimGeneration = null,
      dispatchRetryLeaseUntil = null, dispatchFence = null,
      nextAction = dispatch_due)
    append unique DispatchRetryWork(
      sessionId = turn.sessionId, inputId = input.id, turnId = turn.id,
      operationId = operationId, dispatchStatus = queued,
      dispatchLastResult = none, dispatchAttemptId = null,
      dispatchRetryId = retryId, retryAllowed = true,
      dueAt = now,
      attemptCount = 0, deadline = deadline,
      ownerId = null, ownerFence = null, claimGeneration = null,
      leaseUntil = null, revision = turn.revision,
      expectedBinding = session.currentBinding, candidateBinding = null,
      dispatchFence = null)

reconcileAcceptedDispatch(record):
  if record.dispatchStatus in {dispatched, terminal}:
    return the stored result

  if record.dispatchStatus == unknown and record.retryAllowed == false:
    return stored unknown with nextAction = query_same_dispatch_attempt_or_manual_reconcile

  # This check is before claimDueOrTakeOver: no expired lease is ever minted.
  if now >= record.dispatchDeadline:
    return persistDispatchDeadlineOutcomeIfFenceRecordMatches(
      record, record.dispatchFence, now)

  work = loadDurableRetryWork(record.dispatchRetryId)
  if work is missing:
    require record.dispatchRetryId != null
    require record.retryAllowed == true
    require record.dispatchRetryDueAt is valid
    require record.dispatchStatus in {queued, blocked, unknown}
    require record.sessionId != null and record.dispatchOperationId != null
    require record.dispatchAttemptId is explicit (value or null)
    require record.dispatchRetryId != null and record.retryAllowed == true
    require record.dispatchDeadline != null
    require record.expectedBinding is explicit (value or null)
    require record.candidateBinding is explicit (value or null)
    require record.revision != null
    atomically recreate DispatchRetryWork from the complete canonical record,
      preserving sessionId, operationId, owner/claim/revision, attempt/retry IDs,
      expected/candidate binding, dueAt, deadline, and retryAllowed
  claim = claimDueOrTakeOver(record, work, record.dispatchFence, now, coordinatorId)
  if claim == retry_waiting:
    return retry_waiting
  { work, dispatchFence } = claim

  if record.dispatchStatus == unknown:
    if record.dispatchAttemptId == null:
      return retainUnknownWithoutRetry(record, dispatchFence,
                                       reason = dispatch_outcome_unknown)
    result = effectWithFence(dispatchFence,
      token => queryRunnerDispatch(record.dispatchAttemptId, fenceToken = token))
    if result is unknown:
      if now >= record.dispatchDeadline or record.dispatchAttemptCount >= maxDispatchAttempts:
        return retainUnknownWithoutRetry(record, dispatchFence,
                                         reason = dispatch_outcome_unknown)
      return rescheduleDispatch(record, dispatchFence, work, nextDueAt(record))
    if result is accepted_before_start:
      return persistDispatchedIfFenceMatches(record, dispatchFence,
        result, turnStatus = queued)
    if result is accepted_and_started:
      return persistDispatchedIfFenceMatches(record, dispatchFence,
        result, turnStatus = running)
    if result is terminal:
      return persistTerminalResultIfFenceMatches(record, dispatchFence, result)
    if result is definitely_rejected:
      dispatchFence = persistDefinitelyRejectedIfFenceMatches(record, dispatchFence)
      if now < record.dispatchDeadline and record.dispatchAttemptCount < maxDispatchAttempts:
        return persistBlockedAndSchedule(record, dispatchFence,
                                         reason = temporary_enqueue_blocked)
      return terminalizeDispatchBlocked(record, dispatchFence,
                                        reason = dispatch_retry_exhausted)

  if now >= record.dispatchDeadline or record.dispatchAttemptCount >= maxDispatchAttempts:
    if record.dispatchAttemptId == null or record.dispatchLastResult != definitely-rejected:
      observationFence = dispatchFence
      return retainUnknownWithoutRetry(record, observationFence,
                                       reason = dispatch_outcome_unknown)
    return terminalizeDispatchBlocked(record, dispatchFence,
                                      reason = dispatch_retry_exhausted)

  atomically:
    require Input.acceptance == accepted
    require record.dispatchStatus in {queued, blocked}
    require record.retryAllowed == true
    require record.dispatchRetryId != null
    require work.dispatchRetryId == record.dispatchRetryId
    require work.sessionId == record.sessionId
    require work.operationId == record.dispatchOperationId
    require work.dueAt <= now and work.deadline == record.dispatchDeadline
    increment dispatchAttemptCount
    dispatchAttemptId = stableAttemptId(record.turnId, record.dispatchAttemptCount)
    dispatchFence = dispatchFence with
      dispatchAttemptId = dispatchAttemptId,
      revision = nextRevision(),
      deadline = record.dispatchDeadline,
      expectedBinding = record.expectedBinding,
      candidateBinding = record.candidateBinding,
      bindingAtEffect = currentBinding
    persist dispatchStatus = retrying, dispatchAttemptId, dispatchFence,
      dispatchLastResult = none,
      dispatchRetryState = claimed, dispatchRetryOwnerId = work.ownerId,
      dispatchRetryClaimGeneration = work.claimGeneration,
      dispatchRetryLeaseUntil = work.leaseUntil,
      revision = dispatchFence.revision, session.revision = dispatchFence.revision

  result = effectWithFence(dispatchFence,
    token => Runner.enqueue(record.inputId, record.turnId,
                            record.dispatchAttemptId, token))
  if result is accepted_before_start:
    return persistDispatchedIfFenceMatches(record, dispatchFence,
      result, turnStatus = queued)
  if result is accepted_and_started:
    return persistDispatchedIfFenceMatches(record, dispatchFence,
      result, turnStatus = running)
  if result is terminal:
    return persistTerminalResultIfFenceMatches(record, dispatchFence, result)
  if result is definitely_rejected:
    dispatchFence = persistDefinitelyRejectedIfFenceMatches(record, dispatchFence)
    if now < record.dispatchDeadline and record.dispatchAttemptCount < maxDispatchAttempts:
      return persistBlockedAndSchedule(record, dispatchFence,
                                       reason = temporary_enqueue_blocked)
    return terminalizeDispatchBlocked(record, dispatchFence,
                                      reason = dispatch_retry_exhausted)
  return rescheduleDispatch(record, dispatchFence, work, nextDueAt(record))

persistDefinitelyRejectedIfFenceMatches(record, dispatchFence):
  atomically:
    require fullDispatchFenceMatches(session, record, dispatchFence, now)
    newRevision = nextRevision()
    dispatchFence = dispatchFence with revision = newRevision
    persist record.dispatchLastResult = definitely-rejected,
      record.revision = newRevision, session.revision = newRevision,
      record.dispatchFence = dispatchFence
    update DispatchRetryWork(dispatchFence.dispatchRetryId).revision = newRevision
    return dispatchFence

persistTerminalResultIfFenceMatches(record, dispatchFence, result):
  atomically:
    require fullDispatchFenceMatches(session, record, dispatchFence, now)
    require result.attemptId == dispatchFence.dispatchAttemptId
    require result.outcome in {completed, failed, cancelled}
    newRevision = nextRevision()
    persist dispatchStatus = terminal, dispatchLastResult = accepted,
      retryAllowed = false, blockedReason = null,
      Turn.status = terminal, Turn.outcome = result.outcome,
      Turn.reason = result.reason, Turn.nextAction = inspect_turn,
      Turn.result = if result.outcome == completed then result.result else null,
      record.revision = newRevision,
      session.revision = newRevision
    mark DispatchRetryWork(dispatchFence.dispatchRetryId) = consumed
    clear dispatchRetryId, dispatchRetryKind, dispatchRetryDueAt,
      dispatchRetryState, dispatchRetryOwnerId, dispatchRetryClaimGeneration,
      dispatchRetryLeaseUntil, dispatchFence
  return terminal

persistDispatchedIfFenceMatches(record, dispatchFence, result, turnStatus):
  atomically:
    require fullDispatchFenceMatches(session, record, dispatchFence, now)
    require result is accepted and result.attemptId == dispatchFence.dispatchAttemptId
    newRevision = nextRevision()
    persist dispatchStatus = dispatched, dispatchLastResult = accepted,
      blockedReason = null, Turn.status = turnStatus, Turn.outcome = null,
      Turn.reason = null,
      Turn.nextAction = if turnStatus == queued then await_dispatch else await_turn_result,
      record.revision = newRevision, session.revision = newRevision
    mark DispatchRetryWork(dispatchFence.dispatchRetryId) = consumed
    clear dispatchRetryId, dispatchRetryKind, dispatchRetryDueAt,
      dispatchRetryState, dispatchRetryOwnerId, dispatchRetryClaimGeneration,
      dispatchRetryLeaseUntil, dispatchFence
  return dispatched

persistBlockedAndSchedule(record, dispatchFence, reason):
  atomically:
    require fullDispatchFenceMatches(session, record, dispatchFence, now)
    require record.dispatchAttemptId != null
    require record.dispatchLastResult == definitely-rejected
    require now < record.dispatchDeadline
    require record.dispatchAttemptCount < maxDispatchAttempts
    retryId = stableRetryId(record.turnId, retrySequence = nextRetrySequence(record))
    dueAt = calculateDueAt(record.dispatchAttemptCount, record.dispatchDeadline)
    require dueAt <= record.dispatchDeadline
    newRevision = nextRevision()
    mark DispatchRetryWork(dispatchFence.dispatchRetryId) = consumed
    persist dispatchStatus = blocked, blockedReason = reason,
      dispatchLastResult = definitely-rejected, retryAllowed = true,
      Turn.status = queued, Turn.outcome = null,
      Turn.reason = dispatch_temporarily_rejected,
      Turn.nextAction = retry_dispatch_at_durable_signal,
      dispatchAttemptCount = record.dispatchAttemptCount,
      dispatchAttemptId = record.dispatchAttemptId,
      dispatchDeadline = record.dispatchDeadline,
      dispatchRetryId = retryId, dispatchRetryKind = timer,
      dispatchRetryDueAt = dueAt, dispatchRetryState = pending,
      dispatchRetryOwnerId = null, dispatchRetryClaimGeneration = null,
      dispatchRetryLeaseUntil = null, dispatchFence = null,
      revision = newRevision,
      nextAction = retry_dispatch_at_durable_signal
    append unique DispatchRetryWork(
      sessionId = record.sessionId, operationId = record.dispatchOperationId,
      dispatchStatus = blocked, dispatchLastResult = definitely-rejected,
      dispatchAttemptId = record.dispatchAttemptId, dispatchRetryId = retryId,
      retryAllowed = true, inputId = record.inputId, turnId = record.turnId,
      dueAt = dueAt, attemptCount = record.dispatchAttemptCount,
      deadline = record.dispatchDeadline, ownerId = null,
      ownerFence = null, claimGeneration = null, leaseUntil = null,
      revision = newRevision, expectedBinding = dispatchFence.expectedBinding,
      candidateBinding = dispatchFence.candidateBinding, dispatchFence = null)
  return retry_scheduled

retainUnknownWithoutRetry(record, dispatchFence, reason):
  atomically:
    require fullDispatchFenceMatches(session, record, dispatchFence, now)
    require record.dispatchStatus in {queued, retrying, unknown}
    if record.dispatchRetryId != null:
      mark DispatchRetryWork(record.dispatchRetryId) = cancelled
    newRevision = nextRevision()
    persist dispatchStatus = unknown, dispatchLastResult = unknown,
      retryAllowed = false, Turn.status = unknown, Turn.outcome = null,
      Turn.reason = reason,
      Turn.nextAction = query_same_dispatch_attempt_or_manual_reconcile,
      nextAction = query_same_dispatch_attempt_or_manual_reconcile,
      dispatchFence = null, revision = newRevision, session.revision = newRevision
    clear dispatchRetryId, dispatchRetryKind, dispatchRetryDueAt,
      dispatchRetryState, dispatchRetryOwnerId, dispatchRetryClaimGeneration,
      dispatchRetryLeaseUntil
  return unknown

terminalizeDispatchBlocked(record, dispatchFence, reason):
  atomically:
    require fullDispatchFenceMatches(session, record, dispatchFence, now)
    require Input.acceptance == accepted
    require record.dispatchAttemptId != null
    require record.dispatchLastResult == definitely-rejected
    require now >= record.dispatchDeadline or
            record.dispatchAttemptCount >= maxDispatchAttempts
    if record.dispatchStatus == terminal:
      return the stored terminal result
    newRevision = nextRevision()
    persist dispatchStatus = terminal, retryAllowed = false,
      dispatchLastResult = definitely-rejected, revision = newRevision,
      session.revision = newRevision
    persist Turn.status = terminal, Turn.outcome = blocked
    persist Turn.reason = dispatch_terminal_rejected,
      Turn.nextAction = inspect_or_explicit_requeue,
      Turn.result = null
    persist blockedReason = reason, dispatchFence = null
    persist nextAction = inspect_or_explicit_requeue
    mark DispatchRetryWork(dispatchFence.dispatchRetryId) = cancelled
    clear dispatchRetryId, dispatchRetryKind, dispatchRetryDueAt,
      dispatchRetryState, dispatchRetryOwnerId, dispatchRetryClaimGeneration,
      dispatchRetryLeaseUntil
  return terminal_blocked
```

`claimDueOrTakeOver` returns a newly claimed complete `DispatchFenceToken`; `rescheduleDispatch` and
every helper named above receive that full token, not only a retry ID or owner ID. Each atomically compares
`sessionId/operationId/ownerId/ownerFence/claimGeneration/revision/dispatchAttemptId/
dispatchRetryId/leaseUntil/deadline/expectedBinding/candidateBinding/bindingAtEffect` before the
claim, external enqueue/query, reschedule, or result write. A stale owner, including Owner A after
Owner B takeover or completion, returns `stale_operation_fence`; it cannot overwrite Turn/dispatch
state or create retry work.

The accepted Input and its Turn are never deleted, replaced, or changed to `rejected` by this
path. A temporary `blocked` record is non-terminal and is legal only after a definitely-rejected
attempt; it must schedule another coordinator wake-up;
the coordinator cannot return just because it read `blocked`. A permanent enqueue refusal is
`dispatchStatus=terminal` with Turn `status=terminal`, `outcome=blocked`, a durable
`blockedReason`, fixed attempt/deadline evidence and `nextAction`; it requires the same attempt's
definitely-rejected result, and no automatic retry follows. Unknown acceptance, missing outbox, or
an absent attempt stays `dispatchStatus=unknown` and `Turn.status=unknown`, never terminal blocked.
A coordinator restart scans durable `DispatchRetryWork`, claims due work, and takes over only after
its persisted lease expires. Duplicate outbox delivery, timer firing or command consumption uses
the same `dispatchRetryId` and is idempotent. `nextAction` is only a client instruction; it is never
the durable wake-up and cannot replace the retry work.
A later explicit requeue is a new bounded operation that references the same Input and Turn, and
cannot silently create another Input, Turn, or launch.

Rejected or failed Jobs, `launchRequestId`, launch operation, reason, nextAction, and
unmaterialized reservations remain permanently as minimal tombstones. Existing retention policy
may remove a full transcript or large payload, but it must not remove `launchRequestId`,
`launchOperationId`, JobId, the three reservation/live mappings, terminal outcome, reason,
nextAction, or revision. Reservation IDs are never reclaimed or reused, and they do not create an
empty Session, Input, or Turn. An old `launchRequestId` or `launchOperationId` can only return its
original outcome or an explicit permanent tombstone. A new launch cannot reinterpret it as a
different set of mappings.

The serial constraint within one Session is a lifecycle fact, not a global capacity policy. While
the previous Turn is `queued`, `running`, `outcome_pending`, or `unknown`, a subsequent Input can
only follow steer/queue semantics or be explicitly rejected. It cannot be submitted concurrently
to the same Runtime. #382 owns max-concurrent-runs across Sessions, capacity claim/release, and the
capacity view. #378 defines only the Session queue limit, ordering, and state facts.

### Runtime Recovery State Machine

The recovery state machine consists of Server-arbitrated state, Runner facts, and CAS against the
current binding. It does not depend on client guesses, and it does not treat an HTTP timeout as a
missing Runtime:

```text
Bound
  | disconnect / runner restart / probe unavailable
  v
ObservationUnknown -- authoritative present --> Bound
  | definitely-missing + current generation idle + admission=ready + no unknown effects
  v
RecoveryClaimed -> CandidateCreated -> CAS Rebound
  | definitely-missing but running/outcome_pending/unknown/possible side effect
  v
RecoveryObservationOnly -- query/authoritative result --> ObservationUnknown | Bound
  | concurrent claim / deadline / candidate cleanup failure
  v
RecoveryInProgress | RecoveryFailed | RecoveryExpired | CleanupPending
```

- `Bound`: The current binding is the only target. Normal retry, follow-up, Compact, Reset, and
  Runner restart first reuse it. After reconnecting, the Runner reports the facts for the current
  physical session again.
- `ObservationUnknown`: A disconnect, timeout, unreachable Runner, authorization error, non-404
  error, malformed response, or any result that cannot prove absence. Preserve the original
  binding, do not create a candidate, and do not replay. When current admission cannot be
  confirmed safely, persist `blocked` and a query next action.
- `RecoveryClaimed`: The Runner has explicitly reported that the current Runtime Session does not
  exist. The Server first persists the unique operation owner, ownerFence, claimGeneration,
  deadline, and candidate key. It then creates an empty Session for the same AgentSession and uses
  the full expected binding for CAS. This state is allowed only when the current generation has
  `activity=idle`, has no unknown Input/Turn/dispatch/runtime effect, and has `admission=ready`. A
  second recovery receives only `recovery_in_progress`.
- `RecoveryObservationOnly`: The physical session is confirmed missing, but the current generation
  is running, outcome_pending, or unknown, or an old side effect might have occurred. Preserve the
  original binding, Turn, and operation facts. Set the Session to `admission=blocked` and
  `nextAction=query_runtime_or_force_reset`. This state never creates a candidate, changes the
  binding by CAS, or replays input. The new generation can become ready only after an explicit
  force-reset commits a new binding and context.
- `Rebound`: Candidate Runtime Session creation and atomic binding replacement succeed. Write one
  `session.context_reset(reason=missing-recovery)` event and increment `ContextGeneration`. Keep
  the old Input/Turn mappings, Workspace, target, and public transcript unchanged. Subsequent new
  Inputs and Turns use only the new `ContextGeneration`.
- `RecoveryFailed` / `RecoveryExpired`: Creation, Runner routing, CAS, deadline, or persistence
  failed. Do not fabricate a replacement for the original binding. If the candidate was not
  adopted, discard it idempotently with the same key. When the cleanup result is uncertain,
  preserve `cleanup_pending` and do not allow a second recovery.

Recovery requires authoritative confirmation that the Runtime is missing. A Runner restart,
connection loss, or read timeout alone does not qualify. Each enters `ObservationUnknown` until a
probe from the same Runner reports `present` or `definitely-missing`. If the physical Session
exists, the system must continue using it and cannot create a new one merely because of a
reconnection.

The system can automatically create an empty Runtime Session, replace the binding, and allow later
input only after confirmed absence, when the current generation has `activity=idle`, no unknown
Input/Turn/dispatch/runtime effect, and `admission=ready`. If the current Turn is `running`,
`outcome_pending`, or `unknown`, or if any old Runtime side effect might have occurred, recovery can
only continue observation/query. It preserves the original binding and Turn, and persists
`admission=blocked` and `nextAction=query_runtime_or_force_reset`; it cannot replace the binding
automatically. New Inputs and Turns are allowed only after force-reset commits a new context and
binding. The system never automatically replays an old Input, prompt, tool call, or side effect to
the new Runtime.

Recovery success does not mean that the original Turn succeeded. It means only that the public
AgentSession has a new usable Runtime context. The original Turn becomes `terminal` only after an
authoritative terminal result arrives from the old binding. Late events from the old binding are
dropped because RuntimeSessionId does not match. Recovery failure does not close the AgentSession,
generate a new SessionId, or collapse the error into `Session failed`.

Missing recovery is not an exception for an Unknown Turn. If the old Turn might have produced a
side effect, the Server preserves `unknown` or `outcome_pending`. It does not allow recovery,
Compact, ordinary Reset, or a new Turn to use a gap left by the old Turn to enter the Runtime. Only
an explicit user-selected `force-reset` can establish a new context boundary. The original Turn and
side-effect risk remain, and the full binding tuple, ownerFence, and claimGeneration continue to
guard against late events.

A status query or a retry with the same request identity performs observation only:

- A duplicate request in queued, running, outcome_pending, terminal, or unknown state returns the
  original record without dispatching again.
- Only an authoritative event or explicit user operation can advance unknown. A client retry
  cannot change it to queued.
- When the side-effect result is uncertain, do not replay automatically. The user first queries the
  state. If it remains uncertain, the user follows `next action` to choose Reset or manual
  verification.

### Deterministic Rules for Disconnects, Duplicate Submissions, and Recovery Windows

| Scenario | Preserved facts | Automatic action | Public result |
|---|---|---|---|
| Client disconnects after request | Persisted Input/Turn and original request identity | Do not resend; query after reconnecting | Original InputId/TurnId, with state from current Server facts |
| Duplicate submission with the same request identity | Original Input/Turn | Return the original record; only queued can continue the same dispatch | `accepted` is not duplicated; a side effect is accepted at most once |
| Same text submitted again with a different identity | Original record and distinct new request identity | Do not infer equivalence or deduplicate automatically | Validate the new request normally; reject it or create a new Input as required |
| Runner restart | Current binding | Runner reconnects and probes; reuse if present | Session/Input/Turn IDs do not change |
| Disconnect or timeout | Current binding and possible activity facts | Preserve the binding and enter observation unknown | An active Turn is `unknown`, not idle |
| Runtime confirmed missing with no pending side effect | Session record | Create an empty Runtime, replace the binding by CAS, and start a new ContextGeneration | Same Session; subsequent Turn can be `queued` |
| Absence confirmed but old Turn might have been submitted | Old Turn and its side-effect uncertainty | Do not replace the binding automatically; allow only explicit force-reset | Old Turn is `unknown`, with a query/force-reset next action |
| Recovery failure | Original binding and records | Do not replace the binding or close the Session | `recovery_failed`, with a specific actionable reason |

A recovery window cannot derive a conclusion by polling the wall clock. If the implementation
needs a deadline, it must inject `TimeProvider` or an equivalent fake and persist window expiration
as a recovery result. Tests cannot use sleep or the current time nondeterministically.

Public recovery failures distinguish at least the following actionable semantics. These values are
written to canonical `reason` and `nextAction`; they are not new command syntax:

| Reason | User-visible meaning | Next action |
|---|---|---|
| `recovery_in_progress` | Another recovery operation for the same Session owns the window | Query the original Session/Input/Turn and wait for that operation's result; do not submit again |
| `runtime_unavailable` | Runner or Runtime is temporarily unreachable, so absence is not proven | Wait until the Runner is ready, then query with the original request identity; do not create a new request |
| `runtime_missing_unconfirmed` | The probe result does not prove that the physical session is absent | Continue querying or let the Runner reconnect and probe; do not substitute Reset for factual determination |
| `recovery_failed` | Absence is confirmed, but create/CAS/persistence failed | Check the Runner, Workspace, and permissions; explicitly Reset when `admission=ready`, while preserving original Session diagnostics |
| `turn_outcome_unknown` | The original Turn might have produced a side effect but has no authoritative result | Query the original Turn; if it remains Unknown, verify manually or explicitly force-reset, and never replay automatically |

### Compact, Reset, and the Public Context Boundary

Compact and Reset are AgentSession context-boundary operations. They are not Workflow Actions and
do not create a new AgentSession, AgentJob, Input, or Turn. Both preserve the existing public
transcript, stable IDs, Workspace, target, and accumulated usage. They change only the boundary of
the subsequent Runtime context, and each has an operation fence with `operationId`, expected
binding, owner, ownerFence, claimGeneration, and a bounded deadline.

- Compact asks the Runtime to compact its current context when the Session can safely enter a
  boundary. On success, it continues using the same binding and does not increment
  `ContextGeneration`, but it must persist a ContextBoundary and operation result for that same
  `ContextGeneration`. Subsequent input continues from this successful boundary.
- Reset establishes an empty context. When necessary, it creates a new Runtime Session and replaces
  the entire current binding. It increments `ContextGeneration` but preserves the old transcript,
  old Input/Turn mappings, and logical Session identity.
- Runtime change and confirmed missing recovery increment `ContextGeneration`, as Reset does. New
  Inputs and Turns record only the new `ContextGeneration`. The `ContextGeneration`, TurnId, and
  result of an old Input or Turn are never rewritten.
- While running, `outcome_pending`, Unknown, or inside a recovery fence, the system cannot pretend
  that Compact or Reset completed. It returns the current state and next action. If the provider
  executed the operation but the response was lost, the operation is `unknown`. The system can
  only query the same operation or explicitly retry with the same idempotency key; it cannot repeat
  the operation implicitly.
- By default, preserve the original Unknown facts and do not replay automatically. Only after the
  user explicitly acknowledges the risk and supplies a new `force-reset` operationId can the system
  increment `ContextGeneration` and establish a new context boundary and binding. The old Turn
  remains `unknown`, while the new context can create new Inputs and Turns; neither replaces the
  other. Drop late events from the old binding by checking the full runner/runtime/session/epoch
  tuple and operation fence. The duplicate side-effect risk and next action must be visible.
- If a Compact, Reset, or force-reset response is lost, query the durable operation result with the
  original operationId. A completed operation returns its original ContextGeneration, boundary,
  binding, and mappings. An incomplete operation continues its original phase. A query cannot
  increment `ContextGeneration` again because the client disconnected.
- A boundary record is a public domain fact. It does not put a provider raw event, internal session
  ID, tool detail, or reconstruction diagnostic directly into the public transcript. #384 owns the
  default public transcript projection. #385 owns history and Session timeline presentation. This
  design specifies only that old records remain, boundaries are observable, and internal events do
  not leak.

### Six Acceptance Criteria and Their Scenario and Implementation Mapping

The six acceptance criteria do not define six implementations. Each criterion maps to observable
facts, failure scenarios, and one minimal implementation batch:

| AC | Observable acceptance criterion | Scenario mapping | Implementation batch |
|---|---|---|---|
| AC1 Stable admission identity | Launch and follow-up both have a caller `requestId`. Launch also persists `launchRequestFingerprint`. After response loss, the same launch key and fingerprint return the original rejection/operation; a changed payload returns `idempotency_key_reused`. The unique `(SessionId, requestId)` mapping reliably returns the same Input/Turn. Steer can report accepted only when a replayable effect is persisted with the Input. | Harness, normal launch, same-key retry/response loss, launch-key reuse with changed payload, different key, accept rejection, steer effect response loss | Batch 1 |
| AC2 Turn and input semantics | queued/retrying/blocked/dispatched/terminal/unknown are consistent with Turn status. blocked can retry; terminal blocked cannot. Steer references an existing Turn and converges through a durable steer operation/fence/reportable `pending|accepted|unknown|terminal` effect. Queue full is rejected before admission. | Normal follow-up, steer, steer response loss/restart/duplicate, queued follow-up, queue full, post-acceptance enqueue failure, permanent dispatch failure | Batch 2 |
| AC3 Canonical Server facts | Job status, Session activity/admission, Input acceptance, and Turn result are returned separately. Turn result uses only outcome/reason/nextAction/nullable result. Input/dispatch uses the single schema from conventions. A missing Job mapping is null+reason. attempt/deadline/fence/reason/nextAction/revision/observedAt are traceable. | Canonical read, stale event, separate Job/Session result, dispatch unknown, current activity after force-reset | Batch 3 |
| AC4 Cross-aggregate Launch convergence | AgentJob and Session do not share a transaction. A durable coordinator/outbox uses idempotent commands to converge partial failure to one Job/first Input/Turn mapping. | Launch partial failure, Server restart, Runner submit response loss | Batch 4 |
| AC5 Binding and recovery fence | One Session has only one recovery owner. Every effect compares the full `FenceToken`: session/operation/owner/fence/generation/revision/expected/candidate/lease/deadline. Candidate response loss first calls `getByKey` with the same key; CAS occurs only after the complete identity is persisted. A mismatched candidate enters only independent cleanup. Candidate get/discard, CAS, cleanup deadline/CAS changed, and fail-closed behavior for an old owner are observable. | Concurrent recovery, lease expiry old owner, Server restart, candidate create response loss/get response loss/identity mismatch, candidate CAS failure, cleanup binding changed, Runner handoff, old event | Batch 5 |
| AC6 Context operation and Unknown | Compact, Cancel/stop, and binding operations have fences before and after each effect. Compact does not increment generation. Reset/Runtime change/missing-recovery/force-reset do. Force-reset atomically preserves superseded targets, and `admission=blocked` remains until the binding commits. | Compact success/response loss, provider already executed, cancel stop uncertain, force-reset with ActiveOperation, force-reset with only unknown Turn/Input/dispatch | Batch 6 |

### Observable Invariants and Scenario Matrix

The implementation must make the following invariants observable through Server fakes and
Runner/Runtime fakes:

1. `accepted=true` always has durable InputId and TurnId values. The same key in the
   `(SessionId, requestId)` map always refers to the same pair of IDs. Only a different key creates
   a new Input.
2. An Input belongs to exactly one Turn. Turn order within a Session is durable and cannot be
   reordered.
3. `running` comes only from execution facts for the current binding. An old Runtime event cannot
   change current state.
4. A Turn has no `idle` state. Neither `outcome_pending` nor `unknown` can be reported as terminal or
   safely idle. When activity=idle, no unterminated Turn exists.
5. `admission=blocked` prevents a new Turn, Compact, Reset, and recovery when a side effect is
   unknown. Steer is the only exception, and only when running is known and the Runtime explicitly
   supports steer.
6. A Session has at most one confirmed Runtime execution at a time. Different Sessions can run in
   parallel. This design makes no global capacity assertion from `#382`.
7. Only confirmed missing allows a binding replacement on the same Runner/runtime. Handoff has an
   independent operation. An uncertain error preserves the binding, and side effects are not
   replayed automatically.
8. The unique owner, ownerFence, claimGeneration, revision, expected/candidate binding, and
   leaseUntil/deadline for a recovery/operation/cleanup/stop fence remain queryable after
   concurrency, lease expiry, restart, or CAS failure. A bare generation is not confused with
   ContextGeneration. The complete token is rechecked before and after every Runtime effect.
9. Launch partial failure, enqueue failure after acceptance, and response loss all preserve
   committed facts. A `blocked` retry does not return early. The attempt/deadline limit atomically
   produces terminal blocked and cannot create a second Input, Turn, or side effect.
10. Compact, Reset, and force-reset preserve the old transcript and stable Session ID. Force-reset
    does not change an old unknown Turn. Old events are dropped by the fence, and the risk and next
    action remain visible.
11. Binding replacement does not change AgentSession, Input, Turn, Workspace, target, or the
    existing transcript. Only a handoff operation can change RunnerId.
12. Successful Compact does not increment ContextGeneration, but it has a durable ContextBoundary
    and operation result. Reset, Runtime change, missing-recovery, and force-reset increment it.
    Old Input/Turn mappings do not change.
13. Compact/Reset response loss, active-operation force-reset, and unknown state from an old
    `ContextGeneration` are queryable. Internal Runtime events do not directly become public
    messages. Current activity does not include an old unknown state.
14. Existing cancel/stop semantics do not change. When the stop result is uncertain, the state
    remains Unknown and is not resubmitted automatically. A recovery failure has a specific reason
    and next action, and the AgentSession remains queryable and diagnosable.

| Scenario | Initial conditions | Key assertion |
|---|---|---|
| Normal launch | No binding and an empty Session | Returns stable Job, Session, Input, and Turn IDs; after acceptance, progresses through queued, running, and terminal |
| Normal follow-up | Idle Session with a transcript | Creates no Job; a new Input and Turn execute serially in the same Session |
| Steer | Current Turn is running and the Runtime explicitly supports steer | Associates the new Input with the existing Turn and commits a durable steer operation and effect in the same transaction; creates no second Turn |
| Steer response loss or restart | Steer operation is durable but the Runtime reply is lost or the owner lease expires | Queries the same effect ID; retains accepted only with a replay path for the same identity, otherwise makes the effect Unknown and blocks admission without creating another Input or Turn |
| Queued follow-up | Previous Turn is running | Later Turn remains queued and cannot run until the previous Turn terminates |
| Queue full | Session queued limit reached | Returns `rejected(queue_full)`, persists a fingerprint tombstone, and persists no new Input; changed payload with the same key is always rejected |
| Harness | Server store, Runner and Runtime fakes, outbox, and injectable time run a fixed event order | Uses no real network, process, Runtime, database, or wall clock; every assertion observes durable state and side-effect counts |
| Definite enqueue rejection after acceptance | Session transaction committed and the dispatch attempt returned `definitely-rejected` | Preserves accepted and sets Turn queued plus nonterminal `dispatchStatus=blocked`; the same retry identity and due signal advances to retrying without losing input |
| Unknown delivery after acceptance | Outbox not delivered, response loss, or no attempt evidence | Synchronizes Turn and dispatch to `unknown`, queries the same attempt or requires manual reconciliation, and does not write terminal blocked |
| Permanent dispatch failure | Dispatch attempt reaches the maximum count or deadline | Preserves Input and Turn; persists `dispatchStatus=terminal`, terminal Turn with outcome blocked, attempt count, deadline, reason, and next action; performs no later retry |
| Disconnect | Turn is running and Runner is unreachable | Preserves binding, makes Turn Unknown, and does not redeliver before recovery |
| Duplicate submit | Same `(SessionId, requestId)` after response loss or duplicate submission, and a different key | Same key returns the original Input and Turn without duplicate dispatch; only a different key creates a new Input; changed payload with the same key is rejected |
| Runtime disappears | Probe on the same Runner confirms missing while current generation is idle, has no unknown effect, and has `admission=ready` | Creates an empty Runtime, swaps binding by CAS, and records a context boundary in the same Session |
| Ambiguous disappearance | Timeout, non-404 result, or Runner restart | Does not replace binding or replay; enters observation Unknown |
| Recovery succeeds | Candidate creation and CAS succeed | Public Session remains queryable; the previous unresolved Turn remains terminal or Unknown according to its facts |
| Recovery fails | Create, CAS, or Runner fails | Preserves original binding and returns a specific error and next action |
| Concurrent recovery | Same binding receives two missing observations | Only one durable owner and candidate exist; the other result is `recovery_in_progress` |
| Recovery restart or expiry | Fence is incomplete before restart | Reconciles by phase; after the deadline, original operation becomes terminal blocked or cleanup-pending, independent cleanup advances within a bound, and later recovery is not blocked forever |
| Old owner after lease expiry | Old owner writes a phase, discard, or completion after lease expiry | Takeover increments ownerFence and claimGeneration; every old write returns `stale_operation_fence` and cannot delete an adopted candidate |
| Candidate CAS failure | Candidate exists but expected binding changed | Marks candidate orphaned; independent cleanup compares candidate identity with adopted and current bindings, then safely discards or enters terminal cleanup-pending |
| Force-reset candidate response loss | Create response is lost and candidate existence is unknown | Preserves `response_lost` and calls `getByKey` under the same fence; absent keeps the same operation pending for retry, a second loss remains Unknown, and CAS waits until the complete matching candidate is durable |
| Force-reset candidate identity mismatch | `getByKey` returns a key or binding different from the persisted target | Constructs no CAS; complete provider identity enters independent cleanup, while an unconfirmed identity becomes Unknown and requires operator reconciliation |
| Launch partial failure | Job committed but Session response was lost | Retry of the original request fingerprint and operation returns the same mapping or rejection; creates no second Session or first Turn |
| Acceptance rejection | Prepare-job reserved identity and Session definitely rejects | Job becomes terminal rejected; Session, Input, and Turn are null with reasons; tombstone remains and no dangling live ID exists |
| Submit response loss | Runner may have accepted the first Turn | Turn query returns terminal or Unknown; no implicit second submit occurs |
| Compact | Safe boundary | Preserves transcript, establishes a later context boundary, and leaks no raw Runtime event |
| Compact success boundary | Compact provider succeeds with a confirmed response | Binding and ContextGeneration do not change; persists ContextBoundary and operation result, and later Input uses the same `ContextGeneration` |
| Reset | Safe boundary | Preserves Session and IDs with empty context; replaces binding through operation and CAS |
| Compact or Reset response loss | Provider may have executed but Server cannot confirm | Operation is Unknown; query or explicit retry uses the same operation and does not duplicate implicitly |
| Cancel and stop regression | One queued, running, and Unknown case | Compares the complete stop fence before and after Runtime stop; an uncertain stop remains Unknown and is not resubmitted automatically |
| Unknown force-reset | Previous Turn is Unknown, no ActiveOperation exists, and Input or dispatch side effects exist | Persists the previous target as `UnresolvedTargetRead` in `supersededTargets` and `unresolvedPrevious`; new operation keeps `admission=blocked` until binding and boundary commit, then allows new Inputs and Turns |
| Active-operation force-reset | Compact or Reset response loss makes ActiveOperation Unknown | After risk confirmation, atomically supersedes the operation and every unresolved target; old operation remains Unknown while an independent fence establishes new context and binding |
| Handoff | Session is safely idle and user selects a new Runner | Only explicit handoff can change Runner; target, candidate, and expected binding become durable first, and old events are rejected |
| Rebind | Session is safely idle on the same Runner | Replaces only a Runtime binding on that Runner; rejects Unknown or cross-Runner requests |

### Architecture, Testing, and Implementation Batches

The Server owns admission, IDs, the Session queue limit, binding and operation
CAS, state projection, and recovery arbitration. The launch coordinator only
advances durable events, outbox records, and idempotent commands across
AgentJob and AgentSession. The Runner only executes, probes, creates, reads,
discards, and submits, and it emits facts carrying the complete binding tuple.
The Runtime adapter classifies SDK, file, and protocol errors as present,
definitely missing, or uncertain failure. CLI and Web do not parse internal
event names or reconstruct lifecycles from timestamps or local polling.

Tests inject the Server store, Runner registry, Runtime probe, create, read,
discard, and submit seams, event outbox, idempotency store, and a fake
`TimeProvider`. They use no real network, process, Runtime SDK, file-backed
Session, database, or wall clock. Each scenario fixes the input event order and
asserts durable state, canonical projections, revision and observedAt, and
side-effect counts. Spec tests cover cross-component behavior; unit and
architecture tests cover state machines, binding and operation CAS, projection
mapping, and dependency boundaries. Test duration follows
[`design/testing.md`](testing.md).

Implement in six independently acceptable value batches, one for each AC:

1. **Batch 1 / AC1 stable admission record**: Implement AgentJob launch intent,
   idempotent Session acceptance, stable Job, Session, Input, and Turn mappings,
   and duplicate observation.
2. **Batch 2 / AC2 serial Turns and follow-up**: Implement
   queued, running, outcome_pending, terminal, and Unknown; steer and new-turn
   relationships; the Session queue limit; and durable enqueue failure after
   acceptance.
3. **Batch 3 / AC3 canonical projection**: Server provides four separate facts,
   revision and observedAt, and next action. CLI and Web adapt only the same read
   model.
4. **Batch 4 / AC4 launch coordinator**: AgentJob events and a durable outbox
   drive idempotent Session commands, first-Turn dispatch, and convergence after
   partial failure or Server restart. Aggregates do not share a transaction.
5. **Batch 5 / AC5 deterministic recovery**: Add the complete binding epoch,
   same-Runner confirmed missing, unique owner, deadline, restart
   reconciliation, candidate idempotency, CAS-failure cleanup, and handoff fence.
6. **Batch 6 / AC6 context boundary and Unknown**: Implement Compact and Reset
   operation fences, response-loss Unknown, uncertain stop, and explicit
   force-reset. Preserve the old transcript and Unknown facts and do not replay
   automatically.

#378 depends on the existing Agent configuration and launch contract, but does
not include #377's Agent configuration and launch experience or define a new
Persona or target model. #382 separately owns max-concurrent-runs capacity
claim and release across Sessions, capacity queue views, and policy tests. #384
owns the default public transcript projection. #385 owns history and Session
timeline presentation. #387 owns external API authentication, idempotency, and
resumable reads after disconnection. #378 provides only reusable internal
canonical state at these boundaries and does not predefine their endpoints or
UI.

### Options and Decision

Option A makes the Runtime Session the public Session. Clients query directly
by RuntimeSessionId. When it disappears, Mohist creates a new physical Session
and replays the old transcript. This makes short-term recovery simple but leaks
provider IDs into the public contract, changes logical identity after a Runner
restart, and can duplicate operations when a side effect is uncertain. It also
cannot associate historical Inputs and Turns reliably.

Option B uses AgentSession as the logical identity, with a current binding and
canonical Server state. Inputs, Turns, and results always belong to the
AgentSession. RuntimeSessionId is only the current physical route. Only an
authoritative confirmed-missing result can replace the expected binding by CAS;
old side effects are not replayed, and uncertain results remain Unknown. This
requires additional probe classification, recovery windows, and CAS tests, but
preserves stable IDs, serial execution within a Session, and consistent CLI and
Web behavior. It can also reuse a Runtime that survived a Runner restart.

#378 selects Option B. Its main failure modes are an unreachable Runner,
uncertain probe, failed candidate creation, and CAS conflict. Each preserves the
old binding and reports Unknown or an actionable recovery failure instead of
guessing that recovery succeeded.

## Current Gap

The current implementation has SessionInput, AgentTurn, part of Activity and
Unknown handling, launch and follow-up, and basic Runtime binding. It does not
yet make this target contract canonical across every entry point:

- Launch must still converge the durable client `launchRequestId` to Server
  `launchOperationId` mapping, Job, Session, Input, and Turn reservations,
  durable Session accept or reject outcome, null-with-reason mappings, and
  rejection and failure tombstones into one durable flow. Clients cannot guess
  after response loss.
- Public Input and Turn states must converge on the accepted, turn relation,
  dispatch status and blocked reason, outcome_pending, terminal, and Unknown
  semantics defined here. One canonical Server read model must provide revision,
  observedAt, and nextAction for Job, Session, Input, and Turn.
- Missing-Runtime recovery still needs ownerFence and claimGeneration, lease
  takeover, the complete expected-binding CAS, candidate create, read, discard,
  and cleanup, adopted-candidate protection, restart reconciliation, and
  fail-closed old owners. Same-Runner recovery and Runner handoff remain
  separate.
- Compact, Reset, Runtime change, missing recovery, and force-reset must converge
  on the ContextGeneration and ContextBoundary rules here. ActiveOperation
  operationId, kind, phase, outcome, owner, revision, deadline, and nextAction
  must enter the canonical read model. Compact does not increment generation;
  every other new logical context does. Old Unknown state cannot contribute to
  current Activity after force-reset.
- Web and CLI must adapt only canonical Server state and expose explicit
  force-reset risk confirmation plus query and retry of the original operation.
  They cannot infer results from local logs, HTTP state, or provider events.

### Implementation Gap: Caller Key Is Required

The target behavior requires a nonempty caller `requestId` for follow-up and a
caller `operationId` for Compact, Reset, recovery, handoff, rebind, stop, and
force-reset. A missing key is rejected before admission. Mohist generates no
hidden identity and writes no operation, Input, Turn, candidate, or external
effect. Current routes and Grains still contain paths that generate a hidden
key when the caller omits it. That is follow-on implementation work, not an
alternative to the target contract or evidence that implementation is
complete. The migration boundary is the canonical Server admission and
operation layer. Every entry point passes the caller key first, and that layer
rejects null or empty values before any durable write or external effect. This
gap remains visible until migration completes.

These delivery gaps do not change ownership boundaries. #377 owns Agent
configuration and launch experience and adds no new configuration model here.
#382 owns max-concurrent-runs capacity claim and release across Sessions and the
capacity view. #384 owns the default public transcript projection. #385 owns
history and Session timeline presentation. #387 owns external API
authentication, idempotency, and resumable reads after disconnection. #378
provides only the reusable lifecycle and canonical-state contracts at these
boundaries.
