# Agent Execution Model

This document defines the single Mohist Agent execution path. It separates Agent
work, logical Sessions, physical Runtime Sessions, and Runtime adapters by
ownership. Workflow is an execution origin and orchestrator, not another work
owner. Runtime-specific behavior belongs in [`runtimes/`](runtimes/README.md).
Canonical internal read schemas and fencing belong in [`conventions.md`](conventions.md).
Authentication, transport, and public projections belong in [`agent-api.md`](agent-api.md).

## Design Drivers

Three forces shape the model:

- **Stable identity.** A user must follow one logical conversation when a Runner
  or physical Runtime Session is replaced. Public Session identity cannot belong
  to an external Runtime.
- **Unknown external effects.** A timeout or lost response does not prove that a
  Runtime command failed. Mohist must preserve `unknown` and reconcile the
  original identity before retrying.
- **Cross-owner convergence.** AgentJob owns work lifecycle. AgentSession owns
  conversation state. They do not share a transaction, so durable identities
  and messages must converge observations without moving either decision to the
  other owner.

Exposing a physical Runtime Session as the public Session would remove a binding
layer but would change user identity on replacement and leak provider lifecycle.
Mohist therefore exposes a stable AgentSession and treats the physical Runtime
Session as its replaceable current Binding.

## Ownership and Call Paths

- Mohist Agent owns identity, Instructions, execution configuration, Skills,
  archival, and readiness.
- Workflow owns orchestration state. A `mohist/agent` task names an Agent,
  supplies input and attribution, and consumes the AgentJob result. Mechanical
  Actions do not create AgentJobs.
- AgentJob is the sole top-level execution owner. It owns lifecycle, result,
  retry, and recovery for one work item from any launch origin.
- AgentSession owns Input order, Turns, Transcript, Activity, context, usage,
  and current Binding.
- Runtime Session owns the physical provider Session and execution facts.
- The Runner process owns the Runtime adapter, provider protocol, process
  resources, event reconciliation, and error classification.

There is one work-owner path:

```text diagram
Workflow mohist/agent task / Web / CLI / Connection / event / mention
                         |
                         v
                 Agent AgentJob
                  |           |
                  v           v
          AgentSession    resolved Action
                              |
                              v
                     Runtime adapter --> Runtime Session
```

Every origin enters through the AgentJob launch boundary. The Agent context
validates readiness and snapshots Instructions, execution configuration, Skills,
Runtime, and Workspace for the accepted launch. A missing, archived, or
not-ready Agent fails launch instead of using a Runtime-specific Workflow path.
AgentJob retry preserves that snapshot unless the caller creates a new launch.

A provider adapter may translate ingress and delivery, but it cannot snapshot an
Agent, own a Runtime Session, or decide a work result. Workflow cannot select a
Runtime, construct an Action, dispatch to Runner, or own execution retry.

Direct API authentication and Project authorization finish before resource or
idempotency lookup, admission, durable write, or external effect. Responses and
events expose only the public projection in [`agent-api.md`](agent-api.md).
Canonical models, physical Binding, workspace paths, prompt or memory content,
and Runner control remain private.

There is no Inline Agent or Agent Definition Reference path. A Workflow worker
is a real Mohist Agent. Its `mohist/agent` task creates an ordinary AgentJob and
AgentSession through the same launch boundary as every other entry point.

## Work lifecycle and Session

AgentJob owns pending, running, and terminal work states, success and failure,
and retry or recovery decisions. AgentSession owns ordered SessionInput and
AgentTurn records, Transcript, context, usage, Activity, and Runtime Binding.

The executor reports the work result to AgentJob and conversation facts to
AgentSession. Workflow consumes the AgentJob result and decides whether its Stage
advances, repairs, retries with a new launch, or stops. A Session event cannot
advance a Workflow or make an AgentJob terminal. A work failure may appear in
Transcript, but AgentSession does not arbitrate that result. Business lifecycle
belongs in Issue and Workflow.

Runner opens and attaches the AgentSession through one AgentJob route for every
launch origin. It validates Project identity and accepts the source kinds
`agent-launch`, `agent-connection`, and `workflow`. Runtime events belong to the
initial Turn fixed by the accepted dispatch. Workflow artifacts use frozen
Workflow Run and Action Attempt identity while AgentJob remains the result owner.

A Follow-up is a Session command, not a new dispatch. It appends a SessionInput
to an existing AgentSession and either joins the current Turn through steer or
creates a later Turn. Compact, Reset, recovery, rebind, handoff, and force-reset
also change only the Session.

AgentJob references the first Input and Turn created by launch. A completed
AgentJob means that the launch work returned successfully. It does not close the
AgentSession or establish that the user's broader task is complete. Later
Follow-ups never reopen or rewrite that AgentJob.

Agent launch fixes Instructions, Runtime, Model, Variant, Skills, and Workspace
identity for the Session. Later input uses the same execution snapshot. Policy
changes affect later launches only. The entry point resolves a named Workspace
from its Origin and persists that identity before acceptance. Where an entry
point permits a Workspace override, the caller supplies its name, never a raw
path or Runner default. The Runner may materialize the work directory later.
Workspace resolution and materialization are authoritative in
[`workspaces.md`](workspaces.md#binding-and-resolution).

### Launch convergence

AgentJob and AgentSession have separate write authorities. Launch therefore uses
a durable protocol instead of a cross-aggregate transaction:

```text diagram
+------------------------+
| caller launchRequestId |
+------------+-----------+
             |
             v
   +------------------+    durable accept result
   | AgentJob prepare |<-------------------------+
   +---------+--------+                          |
             |                                   |
             vdurable accept request             |
     +--------------+                            |
     | AgentSession +----------------------------+
     +--------------+
```

- The caller supplies `launchRequestId`. After authentication and authorization,
  Server normalizes the accepted envelope, derives its fingerprint, and never
  trusts a caller-supplied fingerprint.
- One accepted `(launchRequestId, fingerprint)` maps to one
  `launchOperationId` and reserved Job, Session, Input, and Turn identities.
- AgentSession materializes its Session, first Input, first Turn, dispatch fact,
  and accept result together, or records a rejection tombstone without a live
  mapping.
- AgentJob publishes live identities only after the durable accept result.
  Reservation identities are never presented as accepted resources.
- Replaying the same key and fingerprint returns the original outcome. Reusing
  the key for changed input returns `idempotency_key_reused` and creates no work.

The launch projection and null rules are authoritative in
[`conventions.md`](conventions.md#canonical-agentsession-launch-and-turn-result-projections).

### AgentSession invariants

```text diagram
                 owns in order  +--------------+      exactly one    +-----------+
                +-------------->| SessionInput +-------------------->| AgentTurn |
                |               +--------------+                     +-----------+
                |
                |               +------------------+
                +-------------->| Transcript facts |
                |               +------------------+
                |
+--------------+|               +-------------------+
| AgentSession ++-------------->| ContextGeneration |
+--------------+|               +-------------------+
                |
                |               +----------------+                   +-----------------+
                +-------------->| CurrentBinding +------------------>| Runtime Session |
                |               +----------------+                   +-----------------+
                |
                |               +-----------------+
                +-------------->| ActiveOperation |
                                +-----------------+
```

The invariants are:

- `Id`, `Source`, and `WorkDir` do not change during the Session lifecycle.
- Parentage is a child-owned `SessionParentLink`, separate from immutable
  `Source`. The tree contract is authoritative in [`subagents.md`](subagents.md).
- `CurrentBinding` is one complete `(runnerId, runtime, runtimeSessionId,
  bindingEpoch)` tuple. Replacement is atomic and monotonic. AgentSession stores
  no physical Session history.
- A new Session starts at `ContextGeneration=1`. Initial dispatch starts only
  after Binding and Session admission have committed.
- Compact keeps the generation. Reset, Runtime change, confirmed-missing
  recovery, force-reset, handoff, and rebind increment it with their committed
  Context boundary.
- One AgentSession has at most one Runtime execution at a time. Transcript order
  is therefore sufficient for the conversation.
- Each accepted Input has one stable Input ID, caller `requestId`, fingerprint,
  Turn ID, and `ContextGeneration`. It never moves to another Turn or generation.
- A Turn can own multiple steer Inputs. A new-turn Input creates a distinct Turn.
- Capacity rejection occurs before acceptance. Accepted Input cannot be
  discarded, overwritten, or assigned a replacement ID.
- User input contains visible text or an explicit attachment. Attachment-only
  input does not gain a hidden prompt.
- AgentSession has no `completed`, `failed`, `stopped`, or `closed` lifecycle.
- At most one ActiveOperation is open at a time. It cannot be cleared merely
  because its owner disappeared or a response was lost. It remains queryable
  until a definite terminal result or explicit supersession.

Persisted records may contain write-side data needed to enforce these invariants,
but they do not define another schema. Canonical internal fields are defined
only in [`conventions.md`](conventions.md).

### Operation identity

Durable operation identity is fixed before any external effect. Trusted callers
provide the canonical identity, and adapters durably map their public key to it.

- Launch uses caller `launchRequestId`; Server creates one
  `launchOperationId`.
- A new-turn Follow-up and steer use the caller's `requestId` as the operation
  identity.
- Compact, Reset, recovery, rebind, handoff, and force-reset use caller
  `operationId`.
- Direct API Stop maps `Idempotency-Key` to one private operation ID.
- Cascade Stop derives stable per-target identities from the root Session and
  caller key.

The direct API mapping and cascade rules belong to [`agent-api.md`](agent-api.md)
and [`subagents.md#cascade-stop`](subagents.md#cascade-stop). The private
operation ID is never serialized externally.

A missing required key is rejected before durable write or external effect. The
same key and fingerprint return the original operation. A changed fingerprint
conflicts. Internal coordinators persist an identity before sending a command.
Historical operations remain queryable after completion, response loss,
supersession, or restart.

## Activity and Transcript

### Activity

AgentSession has only these Activity states:

- `idle`: no current-generation Turn or operation is nonterminal or uncertain.
  This is the only safe idle state.
- `active`: a Turn is queued, running, or `outcome_pending`, or a known Session
  operation is progressing.
- `unknown`: Input acceptance, a Turn result, a Runtime effect, Binding, or an
  operation cannot be confirmed.

```text diagram
                   +------+                   work settles
                   | idle |<-------------------------------+
                   +---+--+                                |
                       |                                   |
                       vaccepted Input                     |
                  +--------+                     reconciled|
                  | active +<------------------------------++
                  +----++--+                               ||
                       || ^  outcome_pending               ||
                       |+-+                                ||
                       |                                   |
                       veffect uncertain                   |
                  +---------+                              ||
                  | unknown +------------------------------++
                  +---------+
```

An explicit force-reset leaves old facts unknown and starts a new current context.
Activity is derived from the current `ContextGeneration`. Older unresolved facts
remain visible through `unresolvedPrevious`, `unresolvedPreviousCount`, and
`nextAction`; they do not overwrite current Activity.

`admission=ready` requires current Activity `idle`, terminal Turns, no unresolved
external side effect, and no ActiveOperation. Otherwise admission is `blocked`
with a stable reason and next action. New Turn, Compact, Reset, and automatic
missing recovery use this result instead of deriving safety from history.

A steer on a known running Turn is the only ordinary Input exception. It requires
Runtime support, the same complete Binding, and no competing operation. It never
converts `unknown` into safe idle.

### Transcript contract

SessionInput and AgentTurn are child records, not independently mutable
aggregates. AgentSession owns Input order, Turn ownership, and transitions.
Transcript is one flat append-only sequence. IDs associate facts but do not form a
message tree or physical Session history.

`outcome_pending` means Input and dispatch are known but no final Turn result is
recorded. `unknown` means acceptance, side effect, or result cannot be confirmed.
Neither state authorizes an ordinary new Turn or context operation, and neither
is replayed automatically.

A Binding replacement writes this boundary before later input:

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

The boundary means that later Runtime context starts empty. It contains no
physical Session history. Binding-changing operations increment
`ContextGeneration`, commit their Binding and context result, and append the
fact atomically. Compact records a boundary without changing Binding or
`ContextGeneration`.

`session.closed`, `session.followup_completed`, and
`session.followup_failed` are not target event types. Consumers must not infer
current Activity from historical completion, failure, or stop facts.

## Follow-up and Stop

Follow-up has two paths chosen by current state:

- Idle and ready admission creates one Input and one new Turn.
- A running Turn with Runtime steer support accepts one Input on that Turn when
  no operation competes.
- A running Turn without steer support queues a later Turn in Session order when
  capacity permits.
- `outcome_pending`, `unknown`, or an active context operation rejects the
  request without guessing a target Turn.

The Session request map is unique by `(SessionId, requestId)`. Acceptance,
rejection, or uncertainty is persisted under that identity. Same-key replay
cannot create another Input, Turn, or operation. A queue-full rejection occurs
before acceptance; retry after capacity requires a new request ID.

An accepted new-turn Input and its dispatch fact commit before asynchronous
enqueue. Queue or process failure cannot erase acceptance. Dispatch retry uses
the original identity and reports `blocked` or `unknown` instead of minting a
new Turn.

Steer persists the Input, target Turn, effect identity, and replay obligation
together. Response loss first queries the same effect identity. Replay is allowed
only when the adapter can apply that identity idempotently and the fence matches.
The authoritative adapter seam is in
[`conventions.md#durable-steer-adapter-seam`](conventions.md#durable-steer-adapter-seam).

`stop` is the only end-work verb. It targets one frozen Turn or a frozen Session
subtree. A queued Turn is cancelled locally. A running Turn is addressed through
its complete expected Binding, and confirmed Runtime stop records `Cancelled`.
A non-cancellable Runtime answer leaves the Turn running. An unconfirmed result
leaves the Turn and operation `unknown` and reuses the same target identity.
Later Turns and changed Bindings are outside the target.

Stop delivery and reply arbitration use one implementation for request and
recovery paths. Recovery redelivery is bounded by its operation deadline and
settles `blocked` with a stable reason when the deadline expires.

Stopping a Turn does not terminate AgentSession. Stopping the initial Turn may
settle its AgentJob. Stopping a later Turn never rewrites a terminal AgentJob.

## AgentSession origins

Each AgentSession has one immutable Agent origin with the resolved Agent ID. One
Agent can have many AgentJobs and AgentSessions. Agent edits and archival do not
change Session origin or its launch snapshot.

A Workflow launch also records immutable Workflow attribution: `workflowRunId`,
Stage, task, and attempt. Attribution explains why the Agent started; it is not a
second Session origin or work owner. A configured Session name may continue one
logical Session within a WorkflowRun only when Agent and Workspace identities
match. Without an explicit name, each AgentJob receives a distinct Session.

Matching Prompt, model, Runtime, Workspace, or configuration does not merge
origins. An origin-specific route only resolves the canonical Session resource.

## Current Runtime Binding

AgentSession ID is the stable logical identity. `CurrentBinding` is the
replaceable physical routing fact:

```json
{
  "runnerId": "runner-...",
  "runtime": "opencode",
  "runtimeSessionId": "ses_...",
  "bindingEpoch": 7
}
```

Normal execution, retry, Follow-up, Compact, and Runner restart reuse the current
Binding. Reset, Runtime change, confirmed-missing recovery, handoff, rebind, and
force-reset may replace the complete tuple without changing Session identity,
origin, or work directory.

Every replacement compares the complete expected Binding and operation fence,
then commits the candidate Binding, incremented `bindingEpoch`, Session revision,
Context boundary, and post-CAS fence atomically. The returned post-CAS fence is
the only token that authorizes later writes. The comparison and effect protocol
are authoritative in [`conventions.md#canonical-effect-fence`](conventions.md#canonical-effect-fence).

Every Runtime command and event carries the complete Binding tuple plus its Input,
Turn, operation, or dispatch identity when applicable. A late event from an old
Runtime Session, Runner, Binding, operation owner, or revision fails closed. It
cannot change current Activity, Turn, Transcript, or clean up a newer Binding.

The Runtime adapter owns physical Session cache, files, processes, and retention.
Binding replacement does not require AgentSession to retain or query the old
physical Session.

## Runtime Session missing recovery

Missing recovery repairs a current Binding. It is not Prompt replay, Workflow
recovery, or Runner migration. Transport failure, timeout, disconnect, or a
missing local cache entry is not proof that the Runtime Session is absent.

Automatic recovery is allowed only when the same Runner gives deterministic
missing evidence and the current generation is safe: Activity is `idle`,
admission is `ready`, no Turn is running or `outcome_pending`, and no Input,
dispatch, Runtime effect, or operation is `unknown`.

```text diagram
                          +----------------+
                          | CurrentBinding |
                          +--------+-------+
                                   |
                                   vresolve on same Runner
                             +----------+
                             | evidence +-----------------------------------+
                             +-----+----+                                   |
            +----------------------+-----+                                  |
            vready                       vmissing + safe                    |
+----------------------+   +--------------------------+                     |
| reuse CurrentBinding |   | create candidate, stable |                     |
+----------------------+   |           key            |                     |
                           +-------------+------------+                     |
                           +-------------+---------+                        |
                           vcomplete               vuncertain               |
                    +------------+    +-------------------------+ uncertain |
                    | fenced CAS |    | keep old Binding, block |<----------+
                    +------------+    +-------------------------+
```

When recovery is unsafe, Mohist retains the original Binding and Turn, sets
`admission=blocked`, and exposes `query_runtime_or_force_reset`. It must not
infer missing, select another Runner, or replay Transcript.

### Recovery ownership and fencing

Recovery is a durable Session operation. One owner holds a bounded lease and
monotonic `ownerFence` and `claimGeneration`. Restart or takeover continues the
same operation only after the prior lease expires; an old owner fails every write.

Server checks the complete `FenceToken` before an external effect and again
before persisting its result. Runtime validates the same token at its side-effect
boundary. No module may define a shorter recovery fence.

Candidate creation uses a stable key derived from the original operation.
Response loss queries that key before retry. Only a complete candidate for the
expected work directory, Runner, Runtime, and next binding epoch may enter
Binding CAS. An incomplete or uncertain candidate never authorizes adoption.

If ownership, revision, expected Binding, or candidate identity changes, the
candidate is orphaned. Cleanup uses the exact candidate key and Binding. It
proves that the candidate is not current or adopted before discarding it under
its own fence. Uncertain cleanup remains queryable as `cleanup-pending` and does
not keep the original operation active.

### Operation boundaries

Automatic replacement after confirmed missing is allowed for an initial AgentJob
Input not yet submitted and for an idle Follow-up. It is rejected during an
executing Follow-up, for Compact, for a Stop target, and for ordinary Reset.
Reset requires safe admission. `unknown` requires explicit force-reset.

Recovery never reconstructs Runtime context from Transcript. Transcript is an
audit and presentation record, not a command source.

## Context Operations

- `compact` keeps Binding and generation. An unknown result stays on the same
  operation, and missing context cannot be compacted.
- `reset` replaces Binding on the same Runner and Runtime and increments the
  generation. It requires idle, ready admission.
- `recovery` replaces a confirmed-missing Session on the same Runner and Runtime
  and increments the generation. It requires deterministic missing evidence.
- `rebind` replaces Binding on the same Runner and may change Runtime. It is
  explicit and never inferred from reconnect.
- `handoff` replaces Binding on a different Runner and increments the generation.
  It is the only operation that may change `runnerId`.
- `force-reset` replaces Binding after explicit risk acknowledgement and
  increments the generation. It preserves and supersedes old unknown facts.
- Per-target `stop` keeps Binding and generation and acts only on its frozen
  Turn and Binding.

Each context operation has one stable operation ID and request fingerprint before
effect. Replay returns the stored operation. A different intent cannot join or
overwrite another active operation. Detailed fields, null rules, comparison,
and CAS algorithm are defined once in [`conventions.md`](conventions.md).

Compact follows the same effect fence but performs no Binding CAS. Success records
a ContextBoundary in the existing generation. A stale or lost result remains
queryable and cannot be treated as a successful boundary.

### Force-reset

Force-reset is an explicit escape from current-generation `unknown`, not an
automatic recovery or ordinary Reset. It is allowed only when:

1. Current Activity or an ActiveOperation is `unknown` and blocks admission.
2. The caller supplies an operation ID and acknowledges possible duplicate or
   failed external effects.
3. The request revision and `ContextGeneration` still match the canonical read.
4. Mohist can retain old Input, Turn, operation, and Binding facts while creating
   the new Context boundary.

Force-reset records every unresolved current-generation Input, Turn, dispatch
attempt, Runtime effect, and ActiveOperation as a superseded target before
adopting a replacement Binding. It never rewrites an old unknown as success,
failure, cancellation, or proof that the old physical Session disappeared.

A candidate response loss keeps its key. A definitely absent or rejected
candidate may be retried with that key within the original deadline. At the
deadline the operation is `blocked`; an unclassifiable result remains `unknown`.
Only an exact complete candidate enters CAS. A mismatched candidate uses
independent cleanup, and an unconfirmed Binding never enters CAS.

New Input can use the new generation only after Binding and Context boundary
commit. Old unresolved facts remain visible with their original identities and a
risk warning.

## Module Ownership

- Workflow owns Profile, WorkflowRun, Stage and Task ordering, checks, Approval,
  and advancement. It consumes AgentJob results but does not own execution.
- Agent owns Mohist Agent, AgentJob, Action contracts, execution snapshots,
  Runner dispatch, retry, recovery, and result validation.
- Session owns AgentSession identity, source and Workflow attribution, Inputs,
  Turns, Activity, Binding, Transcript, context, and usage.
- Runner executes resolved Agent work and reports capacity and physical facts. It
  does not arbitrate AgentJob or logical Session state.
- Runtime adapters hide provider SDK, protocol, process, cache, and error detail.
  They do not define public Session identity or idempotency.
- Web, CLI, and trusted integrations consume canonical Server projections. Direct
  API callers consume only the public projection in [`agent-api.md`](agent-api.md).

Server is the sole arbiter for Binding, Activity, admission, and operation
results. Runner cannot replace Binding or close AgentSession because a process
exited.

## Public Execution Context

Durable launch metadata may retain a filesystem `workspacePath` for internal
dispatch, recovery, and storage lookup. It is not a public execution-context
fact. Agent-scoped Session lists and summaries expose only Issue, Epic,
Repository, and named Workspace. CLI and Web types consume those read models
and cannot reconstruct or display the materialization path.

## Status

Current implementation gaps are:

- Launch acceptance does not converge through one durable path from caller
  identity to every accepted or rejected Job, Session, Input, and Turn result.
- Trusted clients do not yet consume only canonical internal projections. Direct
  API admission and allowlisted public projection are not enforced end to end.
- Confirmed-missing recovery is not uniform for safely idle AgentJob Input and
  idle Follow-up. Non-idle reconnect reconciliation can replace a Binding without
  proving that an earlier effect is absent.
- Web and CLI do not yet rely exclusively on canonical Server state for recovery,
  force-reset risk confirmation, and original-operation query.
- Some direct launch payloads still carry legacy `workspacePath`; named Workspace
  is the target identity and caller-supplied materialization paths are outside
  the target contract.
- Stop recovery still has divergent request and recovery paths, including a
  synchronous Session-to-AgentJob stop-unknown cycle and no deadline on recovery
  redelivery. The one-way, single-owner, deadline-bounded rules above are the
  target.
- Every Follow-up requires a caller `requestId`. Compact, Reset, recovery,
  handoff, rebind, and force-reset require a caller `operationId`. Some current
  entry points still synthesize a hidden key when the caller omits one, so
  response-loss retry cannot name the original intent.
