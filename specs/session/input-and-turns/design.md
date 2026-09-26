# Input And Turns Design

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
[`workspaces.md`](../../workspace/lifecycle/design.md#binding-and-resolution).

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
[`conventions.md`](../../../design/conventions.md#canonical-agentsession-launch-and-turn-result-projections).

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
  `Source`. The tree contract is authoritative in [`subagents.md`](../subagents/design.md).
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
only in [`conventions.md`](../../../design/conventions.md).

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

The direct API mapping and cascade rules belong to [`agent-api.md`](../../interfaces/agent-api/design.md)
and [`subagents.md#cascade-stop`](../subagents/design.md#cascade-stop). The private
operation ID is never serialized externally.

A missing required key is rejected before durable write or external effect. The
same key and fingerprint return the original operation. A changed fingerprint
conflicts. Internal coordinators persist an identity before sending a command.
Historical operations remain queryable after completion, response loss,
supersession, or restart.

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
[`conventions.md#durable-steer-adapter-seam`](../../../design/conventions.md#durable-steer-adapter-seam).

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

The durable Workflow launch scope is `(projectId, workflowRunId, stage,
actionAttemptId, workId)`. The handoff grain, invocation, AgentJob, initial Input,
and initial Turn derive from that same scope, and the launch fingerprint includes
the scope before rendered command content. Stage is required because task and
work identifiers are local to a Stage and may repeat in another Stage, including
approval-feedback tasks. Retries of one scope reuse its identities; a different
Stage always creates a different invocation, Job, Input, and Turn even when its
task identifier and rendered prompt match.

A named Session follow-up uses the Stage-scoped invocation ID as its idempotency
key. Replaying the same launch therefore returns the same Input and Turn, while
the same task or work ID in another Stage appends a distinct follow-up to the
shared Session. Because Workflow pre-mints that Turn identity, the follow-up
requests a distinct queued Turn instead of coalescing with another pending input.

New launches write only the Stage-scoped handoff. During activation recovery,
an already-running attempt first reads that key and, only when it has no plan,
reads the exact pre-Stage key so work accepted before the identity change can
finish. The recovered plan keeps its original invocation and fingerprint; the
legacy key is never used to prepare new work.

Matching Prompt, model, Runtime, Workspace, or configuration does not merge
origins. An origin-specific route only resolves the canonical Session resource.
