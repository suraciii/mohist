# Agent Execution Model

This document defines why Workflow work, Agent work, logical Sessions, physical Runtime Sessions,
and Runtime adapters have separate owners. Runtime-specific behavior belongs in
[`runtimes/`](runtimes/README.md). Canonical internal read schemas and the complete fencing
protocol belong in [`conventions.md`](conventions.md). Authentication, transport, public replay-key
mapping, and external API projections belong in [`agent-api.md`](agent-api.md).

## Design Drivers

Three forces shape the model:

- **Stable identity.** A user follows one logical conversation even when its Runner process or
  physical Runtime Session is replaced. Public Session identity therefore cannot belong to an
  external Runtime.
- **Unknown external effects.** A timeout or lost response does not prove that a Runtime command
  failed. Mohist must preserve `unknown` and reconcile the original identity before retrying.
  Guessing can duplicate input or apply a destructive context operation twice.
- **Cross-owner convergence.** TaskRun or AgentJob decides work lifecycle, while AgentSession owns
  conversation state. They do not share a transaction. Durable request identity and durable
  messages must converge their observations without moving either decision to the other owner.

Exposing a physical Runtime Session as the public Session would avoid a binding layer, but every
replacement would change user identity and leak provider lifecycle. Mohist instead exposes a
stable AgentSession and treats the physical Runtime Session as its replaceable current Binding.
The cost is explicit operation state and fencing; the benefit is one durable conversation identity
across Runtime loss.

## Ownership and Call Paths

| Concept | Owner | Authoritative decisions |
|---|---|---|
| Mohist Agent | Agent context | identity, Instructions, execution configuration, Skills, archival |
| TaskRun | Workflow context | Workflow task lifecycle, result, retry, recovery, advancement |
| AgentJob | Agent context | lifecycle and result of one Agent work item |
| AgentSession | Session context | Input order, Turns, Transcript, Activity, context, usage, current Binding |
| Runtime Session | external Runtime | physical provider Session and execution facts |
| Runtime adapter | Runner process | provider protocol, process resources, event reconciliation, error classification |

There are three ownership cases, with two execution boundaries:

```text diagram
Inline Workflow TaskRun -- mohist/opencode|pi --> Runtime adapter --> Runtime Session
Workflow TaskRun -- mohist/agent handoff --> AgentJob --> Runtime adapter --> Runtime Session
                                                     |
                                                     +--> AgentSession records conversation facts
```

A Workflow `mohist/opencode` or `mohist/pi` task remains a TaskRun-owned inline
execution. A Workflow `mohist/agent` task is a delegation: preflight freezes the
named Agent definition, workspace, timeout, and completion contract; the durable
handoff mints Job/Session/Input/Turn identifiers; and AgentJob owns admission,
Runner claim, execution, and the terminal Agent result. Workflow remains the
owner of task settlement, `expect`, artifacts, `setVars`, recovery, and
advancement through the typed terminal finalizer. A retry is a new handoff
attempt and resolves the current Agent definition again. A missing or archived
Agent fails dispatch instead of falling back to an inline Runtime path.

The handoff is a BREAKING identifier/status contract. Workflow and Session read
surfaces project the same invocation identity. Invocation status is derived from
the AgentJob ledger plus settlement/task facts: pending but unclaimed, including
permit and Runner waiting, is `queued`; running and non-terminal `Unknown` are
`executing`; terminal AgentJob states are `completed`, `failed`, or `cancelled`;
and a failed terminal with a pending or applying recovery decision is
`recovering`. No handoff-plan status mirror or transcript parsing participates.

Web, CLI, Agent Connection, event routing, and mentions are call origins for the AgentJob path.
They all enter through the canonical AgentJob launch boundary and cannot create a third execution
path. A provider adapter such as Slack may translate ingress and delivery, but it cannot snapshot
an Agent, own a Runtime Session, or decide a work result.

Direct API callers cross an additional trust and projection boundary. Bearer PAT authentication
and Project/scope authorization finish before resource or idempotency lookup, admission, durable
write, or external effect. Responses and events expose only the public projection defined in
[`agent-api.md`](agent-api.md); canonical internal models, physical Binding, workspace paths,
prompt or memory content, and Runner control remain private.

An Inline Agent is a usage mode, not another entity. It is a TaskRun that directly selects a
Runtime-specific Action. The Workflow Action adapter and AgentJob executor may reuse the same deep
Runtime module; they must not reuse each other's work lifecycle.

## Work lifecycle and Session

TaskRun and AgentJob own pending, running, and terminal work states; success and failure; and retry
or recovery decisions. AgentSession owns ordered SessionInput and AgentTurn records, Transcript,
context, usage, Activity, and current Runtime Binding.

The inline Workflow Action adapter reports a work result to TaskRun. The
AgentJob executor reports a work result to AgentJob, and the typed terminal
transport lets the Workflow finalizer apply task effects. Both paths report
conversation facts to AgentSession. A Session event cannot advance a Workflow
or make an AgentJob terminal. A work failure may appear in Transcript, but
AgentSession does not arbitrate either work result.

A Follow-up is a Session command, not a new work dispatch. It appends a SessionInput to an existing
AgentSession and either joins the current Turn through steer or creates a later Turn. It does not
create a TaskRun or AgentJob. Compact, Reset, recovery, rebind, handoff, and force-reset also change
only the Session.

AgentJob references the first Input and Turn created by launch. A completed AgentJob means that the
launch work returned successfully. It does not mean the AgentSession is closed or that the
natural-language task is semantically complete. Later Follow-ups never reopen or rewrite the
original AgentJob. Business lifecycle belongs in Issue and Workflow.

Agent launch fixes Instructions, Runtime, Model, Variant, Skills, and Workspace identity for that
Session. Later input uses the same execution snapshot. Policy changes do not rewrite an execution
that has already started. The entry point resolves a named Workspace from its Origin and persists
that identity before acceptance; CLI, Web, and Slack have different Origin rules. The Runner may
materialize the work directory later. A caller can select a Workspace by Name where the entry point
allows an override, but cannot substitute a raw path or Runner default. Workspace resolution and
materialization are authoritative in [`workspace.md`](workspace.md#binding-and-resolution).

### Launch convergence

AgentJob and AgentSession have separate write authorities, so launch is not one cross-aggregate
transaction. The durable protocol instead makes every partial state queryable:

```text diagram
caller launchRequestId
          |
          v
AgentJob prepare -- durable accept request --> AgentSession
       ^                                        |
       +-------- durable accept result ---------+
```

- The caller supplies a stable `launchRequestId`. After authentication and authorization, Server
  normalizes the complete accepted envelope and derives its fingerprint; a caller-supplied
  fingerprint is never trusted.
- The first accepted `(launchRequestId, fingerprint)` maps once to a Server-owned
  `launchOperationId` and reserved Job, Session, Input, and Turn identities.
- AgentSession either materializes its Session, first Input, first Turn, dispatch fact, and durable
  accept result together, or records a durable rejection tombstone without a live mapping.
- AgentJob publishes live identities only after it receives the durable accept result. Reservation
  identities are never presented as accepted resources.
- Same-key replay with the same fingerprint returns the original outcome. Reusing the key for a
  changed request returns `idempotency_key_reused` and creates no second work item.

The launch projection and null rules are authoritative in
[`conventions.md`](conventions.md#canonical-agentsession-launch-and-turn-result-projections).

### AgentSession invariants

```text diagram
AgentSession (stable logical identity)
  | owns in order
  +--> SessionInput -- belongs to exactly one --> AgentTurn
  +--> Transcript facts
  +--> current ContextGeneration
  +--> CurrentBinding --> one physical Runtime Session
  +--> at most one ActiveOperation
```

The invariants are:

- `Id`, `Source`, and `WorkDir` do not change during the Session lifecycle.
- Optional parentage is a child-owned `SessionParentLink`, separate from immutable `Source`. The
  tree contract is authoritative in [`subagents.md`](subagents.md).
- `CurrentBinding` is one complete `(runnerId, runtime, runtimeSessionId, bindingEpoch)` tuple.
  Replacement changes it atomically and monotonically; AgentSession stores no physical Session
  history.
- A new Session starts at `ContextGeneration=1`. Its Binding may be null before the first execution
  establishes a physical Runtime Session; initial dispatch starts only after Binding and Session
  admission have both committed.
- Compact keeps the current generation. Reset, Runtime change, confirmed-missing recovery,
  force-reset, handoff, and rebind increment it only with their committed Context boundary.
- One AgentSession has at most one Runtime execution at a time. Transcript order is therefore
  sufficient for the conversation.
- Each accepted Input has one stable Input ID, caller `requestId`, fingerprint, Turn ID, and
  `ContextGeneration`. It never moves to another Turn or generation.
- A Turn can own multiple steer Inputs, but a new-turn Input creates a distinct Turn.
- Capacity rejection occurs before acceptance. Once accepted, an Input cannot be discarded,
  overwritten, or assigned a replacement ID.
- User input contains visible text or an explicit attachment. Attachment-only input does not gain
  a hidden prompt.
- AgentSession has no `completed`, `failed`, `stopped`, or `closed` lifecycle.
- An ActiveOperation cannot be cleared merely because its owner disappeared or a response was
  lost. It remains queryable until it reaches a definite terminal result or is explicitly
  superseded under the canonical operation contract.

The persisted domain records may contain write-side data needed to enforce these invariants, but
they do not define another schema. Canonical internal Session, Input, Turn, dispatch, operation,
and fence fields are defined only in [`conventions.md`](conventions.md).

### Operation identity

Durable identity is fixed at ingress before any external effect. Trusted callers provide the
canonical command identity directly; an external adapter durably maps its caller-held public key
to that identity before invoking the command.

| Intent | Ingress replay identity | Durable operation identity |
|---|---|---|
| Launch | caller `launchRequestId` | Server creates and durably maps one `launchOperationId` |
| Follow-up with new Turn | caller `requestId` | the same request map identity |
| Steer | caller `requestId` | the same `SessionOperationRead.operationId` |
| Compact, Reset, recovery, rebind, handoff, force-reset | caller `operationId` | the same operation ID |
| Direct API Turn stop | caller `Idempotency-Key` | adapter maps one private operation ID for the frozen Turn |
| Cascade stop | root Session plus caller `Idempotency-Key` | Server derives the tree operation and stable per-target operation IDs |

The direct API mapping, authentication scope, and response contract are authoritative only in
[`agent-api.md`](agent-api.md). The private operation ID is never serialized externally; replay or
query resolves the original public key to that same identity. Cascade membership and its derived
per-target identities remain authoritative in
[`subagents.md#cascade-stop`](subagents.md#cascade-stop). A direct Turn stop does not redefine the
cascade contract.

For caller-owned keys, a missing key is rejected before a durable write or external effect. The
same key and fingerprint return the original operation; a changed fingerprint conflicts. Internal
coordinators must persist an identity before sending a command. They cannot leave a caller waiting
on an unqueryable effect.

One AgentSession has at most one active Session operation. Historical operations remain queryable
by their original identity after completion, response loss, supersession, or restart. The canonical
operation projection and kind-specific rules are in
[`conventions.md#canonical-sessionoperationread`](conventions.md#canonical-sessionoperationread).

## Activity and Transcript

### Activity

AgentSession has only these Activity states:

| Value | Meaning |
|---|---|
| `idle` | No current-generation Turn or operation is nonterminal or uncertain. This is the only safe idle state. |
| `active` | A Turn is queued, running, or `outcome_pending`, or a known Session operation is progressing. |
| `unknown` | Input acceptance, a Turn result, a Runtime effect, Binding, or an operation cannot be confirmed. |

```text diagram
idle -- accepted Input ----------------------------> active
active -- all current work settles definitively ---> idle
active -- final result still expected -------------> active (outcome_pending)
active -- acceptance or effect becomes uncertain --> unknown
unknown -- authoritative reconciliation -----------> active | idle | unknown
unknown -- explicit force-reset --------------------> unknown old facts + new current context
```

Activity is derived from the current `ContextGeneration`. Unresolved facts from older generations
remain visible through `unresolvedPrevious`, `unresolvedPreviousCount`, and `nextAction`; they do
not overwrite `currentContextActivity`.

`admission=ready` requires all of the following in the current generation: Activity is `idle`, all
Turns are terminal, no external side effect is unresolved, and there is no ActiveOperation.
Otherwise admission is `blocked` with a stable reason and next action. An ordinary new Turn,
Compact, Reset, or automatic missing recovery must use this canonical admission result rather than
rederive safety from historical events.

A steer on a known running Turn is the only ordinary Input exception. It still requires explicit
Runtime support, the same complete Binding, and no competing operation. It never converts
`unknown` into safe idle.

### Transcript contract

SessionInput and AgentTurn are child records, not independently mutable aggregates. AgentSession is
the only authority for Input order, Turn ownership, and transitions. Transcript is one flat,
append-only sequence of Session facts; IDs provide stable association but do not form a message
tree or physical Session history.

`outcome_pending` means Input and dispatch are known but no final Turn result is recorded.
`unknown` means acceptance, side effect, or result cannot be confirmed. Neither state authorizes an
ordinary new Turn or context operation, and neither is replayed automatically.

A Binding replacement writes this user-visible boundary before later input:

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

The boundary means only that later Runtime context starts empty. It contains no physical Session
history. Reset, Runtime change, missing recovery, force-reset, handoff, and rebind increment
`ContextGeneration`, commit their Binding/context result, and append this fact atomically. Compact
records a boundary result without changing the Binding or `ContextGeneration`.

`session.closed`, `session.followup_completed`, and `session.followup_failed` are not target event
types. Input and Turn express acceptance and execution separately. Consumers must not infer current
Activity from historical completion, failure, or stop facts.

## Follow-up and Stop

Follow-up has two semantic paths:

| Current state | Accepted relation | Result |
|---|---|---|
| idle, admission ready | `new-turn` | create one Input and one new Turn |
| running, steer supported, no competing operation | `steer` | create one Input attached to the current Turn |
| running, steer unsupported | `new-turn` | queue a later Turn in Session order if capacity permits |
| `outcome_pending`, `unknown`, or context operation active | none | reject without guessing a target Turn |

The Session request map is unique by `(SessionId, requestId)`. Acceptance, a durable rejection
tombstone, or an uncertain result is persisted under that identity. Same-key replay reads the
stored Input/Turn/operation and cannot create another one. A queue-full rejection occurs before an
Input is accepted; retry after capacity requires a new request ID.

An accepted new-turn Input and its dispatch fact commit before asynchronous enqueue. Queue or
process failure after that point cannot erase acceptance. Dispatch retry uses the original
attempt/work identity, remains bounded, and exposes `blocked` or `unknown` instead of minting a new
Turn. Dispatch schema and retry fencing are authoritative in
[`conventions.md#canonical-sessioninput-and-dispatch-schema`](conventions.md#canonical-sessioninput-and-dispatch-schema).

Steer persists the Input, target Turn, operation/effect identity, and replay obligation together.
Only a confirmed or safely replayable effect can be reported as accepted. Response loss first
queries the same effect identity; replay is allowed only when the adapter can apply that identity
idempotently and the complete fence still matches. A terminal or superseded target settles the
steer operation without moving the accepted Input. The authoritative adapter seam and result
mapping are in [`conventions.md#durable-steer-adapter-seam`](conventions.md#durable-steer-adapter-seam).

`stop` is the only end-work verb; `cancel` is retired from the verb set and survives only as the
`Cancelled` terminal outcome. Stop has two scopes: one frozen Turn (CLI `--turn-id`, or a direct
API stop through the external key mapping in [`agent-api.md`](agent-api.md)), or a frozen Session
subtree under [`subagents.md#cascade-stop`](subagents.md#cascade-stop). The engine, not the caller,
selects the mechanism from Turn state; each frozen target uses the same canonical rules below:

- a queued Turn ends locally without contacting Runner and is recorded `Cancelled` in the same
  Session transaction;
- a running Turn is addressed only through its snapshotted Turn and complete expected Binding;
- Runtime stop is fenced before and after the external effect, and a confirmed stop records the
  Turn `Cancelled`;
- a Runtime not-cancellable answer means the Turn is still executing: the operation reports
  not-cancellable honestly and never reports a running Turn as stopped;
- an unconfirmed result leaves the target Turn and operation `unknown` and reuses the same derived
  target identity for query or bounded retry;
- a later Turn or changed Binding is outside the target and cannot be stopped by stale work.

Stop delivery and reply arbitration have exactly one implementation, shared by the request path
and the recovery path; an unconfirmed result behaves identically on both, including bound-work
abandonment at settlement. Recovery redelivery is bounded by the operation deadline: exhausting it
settles the operation `blocked` with a stable reason instead of retrying without limit.

Stopping a Turn does not terminate AgentSession. Stopping the initial Turn may settle its AgentJob;
stopping a later Turn never rewrites an already terminal AgentJob.

## AgentSession origins

Each AgentSession has exactly one immutable origin.

### Workflow origin

Address an inline Workflow-origin Session by `(projectId, workflowRunId,
sessionName)`; inline tasks may continue a logical Session when they reuse a
name. A `mohist/agent` handoff keeps the Workflow origin but resolves the named
`session` as a label only, defaulting to the Work ID. Each attempt mints an
independent AgentSession, first SessionInput, and first AgentTurn. Reusing the
label never merges delegated attempts. Reciprocal labels carry the
WorkflowRun/TaskRun/work/invocation lineage, and the read surfaces carry the
same Job/Session/Input/Turn identifiers.

### Agent launch origin

Each Mohist Agent launch creates an Agent origin with the resolved Agent ID. One Agent can have many
AgentJobs and AgentSessions. Later Agent edits or archival do not change the Session origin or its
launch snapshot.

Matching Prompt, model, Runtime, Workspace, or configuration does not merge origins. An
origin-specific route is only a query convenience; it resolves to the canonical Session resource
and cannot define another lifecycle.

## Current Runtime Binding

AgentSession ID is the stable logical identity. `CurrentBinding` is the replaceable physical
routing fact:

```json
{
  "runnerId": "runner-...",
  "runtime": "opencode",
  "runtimeSessionId": "ses_...",
  "bindingEpoch": 7
}
```

Normal execution, retry, Follow-up, Compact, and Runner restart reuse the current Binding. Reset,
explicit Runtime change, confirmed-missing recovery, handoff, rebind, and force-reset may replace
the complete tuple without changing Session identity, origin, or work directory.

Every replacement compares the complete expected Binding and current operation fence, then commits
the candidate Binding, incremented `bindingEpoch`, Session revision, Context boundary, and post-CAS
fence as one atomic change. The returned post-CAS fence is the only token that can authorize later
writes. The canonical comparison and effect protocol are authoritative in
[`conventions.md#canonical-effect-fence`](conventions.md#canonical-effect-fence).

Every Runtime command and event carries the complete Binding tuple plus its Input, Turn, operation,
or dispatch identity when applicable. A late event from an old Runtime Session, Runner, binding
epoch, operation owner, or revision fails closed. It cannot change current Activity, Turn,
Transcript, or clean up a newer Binding.

The Runtime adapter owns physical Session cache, files, processes, and retention. Binding
replacement does not require AgentSession to retain, close, or continue querying the old physical
Session.

## Runtime Session missing recovery

Missing recovery repairs a current Binding; it is not Prompt replay, Workflow recovery, or Runner
migration. Transport failure, timeout, disconnect, or a missing local cache entry is not proof that
the Runtime Session is absent.

Automatic recovery is allowed only when the same Runner gives deterministic evidence that the
current Runtime Session is missing and the current generation is otherwise safe: Activity is
`idle`, admission is `ready`, no Turn is running or `outcome_pending`, and no Input, dispatch,
Runtime effect, or operation is `unknown`.

```text diagram
CurrentBinding
  | resolve on the same Runner
  +-- ready --------------------------> reuse CurrentBinding
  +-- definitely missing + safe -----> create candidate with stable key
  |                                      |
  |                                      +-- complete candidate --> fenced CAS
  |                                      +-- uncertain ----------> keep old Binding, block
  +-- absent evidence or uncertain ---> keep old Binding, block
```

When recovery is unsafe, Mohist retains the original Binding and Turn, sets
`admission=blocked`, and exposes `query_runtime_or_force_reset`. It must not infer missing, select a
different Runner, or replay Transcript.

### Recovery ownership and fencing

Recovery is a durable Session operation, not an in-memory window. One owner holds a bounded lease
and monotonically increasing `ownerFence` and `claimGeneration`. Restart or takeover can continue
the same operation only after the prior lease expires; an old owner then fails every write.

All external effects and persistence results use the complete `FenceToken` and `fenceMatch` from
[`conventions.md#canonical-effect-fence`](conventions.md#canonical-effect-fence). The Server checks
the token before the effect and again before persisting its result. Runtime validates the same
token at its side-effect boundary. No module may define a shortened recovery fence.

Candidate creation uses a stable key derived from the original operation. Response loss queries
that key before any retry. Only a complete candidate for the expected work directory,
runner/runtime, and next binding epoch may enter Binding CAS. An absent, rejected, incomplete, or
unknown candidate never authorizes adoption.

If ownership, revision, expected Binding, or candidate identity changes before adoption, the
candidate is an orphan. Cleanup uses an independent bounded operation and the exact candidate key
and Binding. It first proves that the candidate is not current or adopted, then discards it under
its own fence. Uncertain cleanup remains queryable as `cleanup-pending`; it does not keep a terminal
original operation active or block a later safe binding operation.

### Operation boundaries

| Operation | Automatic replacement after confirmed missing | Reason |
|---|---:|---|
| Initial TaskRun or AgentJob Input not yet submitted | Yes | It can continue in a known empty context without replaying an effect |
| Idle Follow-up | Yes | It starts a new execution through the same acceptance identity |
| Follow-up during execution | No | Replacement would change the physical target of that Input |
| Compact | No | A missing context cannot be compacted |
| Stop target | No | A replacement is not the original execution target |
| Ordinary Reset | No | Reset requires safe admission; unknown requires explicit force-reset |

Recovery never reconstructs Runtime context from Transcript. Transcript is an audit and
presentation record, not a command source.

## Context Operations

| Kind | Binding effect | Context effect | Key safety rule |
|---|---|---|---|
| `compact` | keep Binding | keep generation | unknown result stays on the same operation; do not compact a missing context |
| `reset` | replace on same Runner/runtime | increment generation | requires idle, ready admission |
| `recovery` | replace confirmed-missing Session on same Runner/runtime | increment generation | deterministic missing evidence only |
| `rebind` | replace on same Runner; runtime may change | increment generation | explicit request; never inferred from reconnect |
| `handoff` | replace on an explicit different Runner | increment generation | only this operation may change `runnerId` |
| `force-reset` | replace after explicit risk acknowledgement | increment generation | preserves and supersedes old unknown facts |
| per-target `stop` | keep Binding | keep generation | derived by cascade or direct API mapping; acts only on frozen Turn/Binding |

Each context operation has one stable canonical operation ID and request fingerprint before any
effect. Internal callers supply that ID, cascade derives its per-target IDs, and the direct API
durably maps its public key. Replay first returns the stored operation. A different intent cannot
join or overwrite another active operation.

Binding-changing operations follow one semantic order:

1. Persist operation identity, fingerprint, expected revision/generation/Binding, owner lease,
   target, stable candidate key, and deadline before any external effect.
2. Create or query only that candidate identity under the complete fence.
3. Record a validated complete candidate before attempting CAS.
4. Atomically change expected Binding to candidate and persist the post-CAS fence and Context
   boundary.
5. Use only the post-CAS fence for later input, result, completion, or cleanup decisions.

The detailed field set, null rules, comparison predicate, and CAS algorithm are defined once in
[`conventions.md`](conventions.md). This document does not restate their storage procedure.

Compact follows the same before/after effect fence but performs no Binding CAS. Success records a
ContextBoundary in the existing generation. A stale or lost result remains queryable on the same
operation and cannot be treated as a successful boundary.

### Force-reset

Force-reset is an explicit escape from current-generation `unknown`, not an automatic recovery or
ordinary Reset. It is allowed only when:

1. canonical current Activity or an ActiveOperation is actually `unknown` and blocks admission;
2. the caller supplies a new force-reset operation ID and explicitly acknowledges possible failure
   or duplicate external effects;
3. the request carries the revision and `ContextGeneration` from the same canonical read, and both
   still match;
4. Mohist can retain the old Input, Turn, operation result, and Binding while creating the new
   Context boundary.

Force-reset atomically records every current-generation unresolved Input, Turn, dispatch attempt,
Runtime effect, and ActiveOperation as a superseded target before adopting a replacement Binding.
It never rewrites an old unknown as success, failure, cancellation, or proof that the old physical
Session disappeared.

Candidate response loss keeps the same candidate key. A definitely absent or rejected candidate
may be retried with that key only within the original deadline; at the deadline the operation is
terminal `blocked`. An unclassifiable result remains `unknown`. Only an exact complete candidate
can enter CAS. A mismatched complete candidate enters independent cleanup; an unconfirmed binding
never does.

New Input can use the new generation only after the replacement Binding and Context boundary
commit. Response-loss query or retry returns the same operation, generation, binding epoch, and
superseded mapping. Old unresolved facts remain visible through `unresolvedPrevious` with their
original identities and a risk warning.

## Module Ownership

- Workflow owns TaskRun, the Workflow Action contract, delegated-task settlement,
  recovery, and advancement. It does not interpret Transcript.
- Agent owns Mohist Agent and AgentJob admission, execution, and terminal result.
  It does not derive work results from Session Activity.
- Session owns AgentSession identity, source, work directory, Inputs, Turns, Activity, Binding,
  Transcript, context, and usage. Its lineage labels point back to a Workflow
  handoff but do not transfer work ownership.
- Runner executes resolved work and reports physical facts. It does not arbitrate logical Session
  state.
- Runtime adapters hide provider SDK, protocol, process, cache, and error details. They do not
  define public Session identity or idempotency.
- Web, CLI, and trusted integrations consume canonical internal Server projections. Direct API
  callers consume only the public projection in [`agent-api.md`](agent-api.md). Neither derives
  current state from logs, provider responses, or historical terminal events.

Server is the sole arbiter for Binding, Activity, admission, and operation results. Runner cannot
independently replace a Binding or close an AgentSession because a process exited.

## Verification Boundaries

Tests must prove the contracts at deterministic seams without real Runtime, network, process,
filesystem Session, or wall clock:

- duplicate launch and Follow-up identities converge without duplicate Job, Session, Input, Turn,
  dispatch, or Runtime effect;
- work lifecycle and Session Activity never overwrite each other;
- `outcome_pending` and `unknown` never appear as idle or terminal success;
- stale owner, revision, Binding, Turn, and candidate facts fail closed before and after effects;
- response loss queries the same operation/candidate identity before any bounded retry;
- Binding replacement atomically advances epoch/generation and keeps Transcript and Session ID;
- cleanup cannot discard an adopted/current candidate;
- cascade stop acts only on frozen targets and never follows later Turns or Bindings;
- force-reset preserves old unknown facts and exposes the supersession mapping.

## Status

Stable AgentSession identity, Input and Turn records, Activity and unknown handling, launch and
Follow-up paths, and current Runtime Binding are implemented. The remaining gap is convergence:
not every ingress, aggregate boundary, and client consumes the same canonical operation and read
model yet.

- Launch acceptance does not yet converge through one durable path from caller identity to every
  accepted or rejected Job/Session/Input/Turn result.
- Canonical internal projections are not yet the only state consumed by trusted clients. The
  direct API does not yet enforce its PAT-first admission and allowlisted public projection from
  [`agent-api.md`](agent-api.md) end to end.
- Confirmed-missing recovery is available for safely idle Workflow input. AgentJob initial Turns
  are already queued before that boundary, and idle Follow-up does not yet initiate it. Non-idle
  reconnect reconciliation can replace a binding without the complete proof that an old effect is
  absent. These paths do not yet share the owner lease, fence, candidate reconciliation, and cleanup
  contract.
- Web and CLI do not yet rely exclusively on canonical Server state for recovery, force-reset risk
  confirmation, and original-operation query.
- Some direct launch payloads still carry the legacy `workspacePath` context field. The named
  Workspace is the target identity; caller-supplied materialization paths are not part of the
  target contract.
- Stop recovery coordination still deviates from the rules above and from
  [`issue-coordination.md`](workflow/issue-coordination.md): Session-to-AgentJob stop-unknown
  propagation closes a synchronous cycle back into the same Session activation; stop delivery and
  reply arbitration are implemented twice (request path and recovery path) with divergent
  unconfirmed-result behavior; recovery redelivery has no deadline. The one-way, single-owner,
  deadline-bounded rules are the target.
- `IAgentJobGrain.ReconcileRunningAsync` is a public reconcile entry that was never wired: it is a
  default no-op with no caller. Removing it is the target.

Every Follow-up requires a non-empty caller `requestId`. Compact, Reset, recovery, handoff, rebind,
and force-reset require a caller `operationId`; steer reuses its Follow-up `requestId`. Some current
entry points still synthesize a hidden key when the caller omits one. This remains a safety gap
because response-loss retry cannot name the original intent.
