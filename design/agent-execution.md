# Agent Execution Model

This document defines the shared abstraction boundaries for Workflow, Agent, Session, Runner, and
Runtime adapters. Runtime-specific behavior belongs in [`runtimes/`](runtimes/README.md), for example
[`runtimes/opencode.md`](runtimes/opencode.md).

## Design Drivers

Three forces shape the execution model:

- **Stable identity.** A user follows one logical conversation even when its Runner process or physical
  Runtime Session is replaced. Public identity therefore cannot be owned by an external Runtime.
- **Unknown external effects.** A timeout or lost response does not prove that a Runtime command failed.
  Mohist must preserve `Unknown` and reconcile before retrying, because replay can duplicate a turn or
  apply a destructive context operation twice.
- **Cross-aggregate convergence.** Work owners decide work lifecycle while AgentSession owns conversation
  state. They do not share a transaction, so durable request identity and durable messages must converge
  their observations without giving either aggregate authority over the other.

Two identity models were considered. Option A exposes the physical Runtime Session as the public Session.
It is initially simpler, but replacement changes the user's identity, leaks provider-specific lifecycle,
and makes missing-session recovery indistinguishable from starting unrelated work. Option B exposes a
stable logical AgentSession and treats the physical Runtime Session as its replaceable current binding.
This requires fencing and recovery state, but it preserves one conversation identity and one ordering
authority across Runtime loss. Mohist selects Option B.

The canonical read schemas, `FenceToken`, fence comparison, and binding compare-and-swap contract are
defined only in [`conventions.md`](conventions.md). This document explains why execution needs those
mechanisms and where their authority applies.

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
[`../docs/actions/agent.md`](../docs/actions/agent.md).

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

## Launch convergence

AgentJob and AgentSession have separate write authorities, so launch cannot be made atomic with one
cross-aggregate transaction. The design instead makes partial progress durable and queryable. That keeps
work lifecycle in AgentJob while Session acceptance remains authoritative in AgentSession.

```text
caller identity
      |
      v
AgentJob prepare ---- durable accept request ----> AgentSession
      ^                                            |
      +----------- durable accept result ----------+
```

- The caller supplies `launchRequestId` and a request fingerprint. Their first accepted use maps once to
  a Server-owned `launchOperationId` and reserved Job, Session, Input, and Turn identities.
- A durable request asks AgentSession to accept the launch. One Session transaction either materializes
  the Session, first Input, first Turn, dispatch record, and durable accept-result message together, or
  records a durable rejection tombstone with no live mapping.
- A durable result maps the accepted identities or rejection outcome back to AgentJob. Reservation IDs
  never appear as live IDs; a successful launch exposes Session, Input, and Turn only as one accepted set.
- Retries and queries use the same caller identity. The same fingerprint converges on the same outcome;
  reusing the key with different intent returns `rejected(idempotency_key_reused)` and never creates a
  second work item or conversation.

The public launch projection and its null rules are defined only in
[`conventions.md`](conventions.md#canonical-agentsession-launch-and-turn-result-projections).

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
  capacity is insufficient, a new `new-turn` Input is rejected before acceptance. Cross-Session capacity
  claim, release, and capacity views belong to the global scheduling policy.
- The bounded queue limit for one Session is an admission invariant in this design. Its specific value is
  a runtime parameter. This document does not copy the global scheduling policy.
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
queued admission for one Session. Cross-Session concurrency, capacity claim and release, and capacity views
belong to the global scheduling policy. This design does not copy that policy.

Cross-Session capacity waiting is canonical Server admission state. A launch, or a Follow-up that starts a
new Runtime execution, first persists stable Input and Turn identities and then claims capacity from the
per-Agent capacity authority. When the claim returns `waiting`, the Turn remains observably `queued` and
retains the same claim token. Capacity release or a policy change wakes that original waiter. It must not be
projected as Ready or a synchronous failure, and it must not require the client to generate a new request
identity. One Session still executes at most one Turn at a time; different Sessions may execute concurrently
within `max-concurrent-runs` and Runner capacity.

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
kind, Session terminal state, or tree-specific delivery path. See
[`scheduled-input.md`](scheduled-input.md) for the complete contract.

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

## Status

The stable AgentSession identity, Input and Turn records, Activity and Unknown handling, launch and
follow-up paths, and a current Runtime Binding are implemented. The remaining gap is convergence:
the same contract is not yet enforced at every ingress, aggregate boundary, and client.

- Launch acceptance is not yet one durable convergence path from caller identity to the accepted Job,
  Session, Input, and Turn outcome. Until it is, response loss can still leave callers without a
  definitive mapping or rejection result.
- Canonical Job, Session, Input, Turn, and operation projections are not yet the only source consumed by
  clients. Parallel interpretations can still disagree about `Unknown`, blocked admission, or the next
  safe action.
- Confirmed-missing recovery does not yet enforce the complete ownership lease, stale-owner fence,
  expected-binding comparison, candidate cleanup, and adopted-candidate protection at every boundary.
  Until those checks converge, replacement cannot be treated as uniformly fail-closed.
- Compact, Reset, Runtime change, missing recovery, and force-reset do not yet share one operation and
  ContextGeneration projection everywhere. The distinction between an in-place context boundary and a
  new logical context must remain observable to every client.
- Web and CLI do not yet rely exclusively on canonical Server state for recovery, explicit force-reset
  risk confirmation, and retry or query of the original operation.

### Caller-owned idempotency keys

Every Follow-up requires a non-empty caller `requestId`. Compact, Reset, recovery, handoff, rebind, stop,
and force-reset require a caller `operationId`. Missing keys are rejected before admission, with no
durable write or external effect.

Some entry points still synthesize an internal key when the caller omits one. This is a safety gap: after
response loss the caller cannot name and query the original intent, so a retry cannot be proven to refer
to the same effect. The gap closes only when every ingress rejects a missing key before any durable or
external side effect.
