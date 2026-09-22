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

Execution resolution is an accepted caller hint where the entry point allows
one, then the Agent definition, then Runtime behavior for unset fields: an unset
Runtime resolves to `pi`, and the Runtime chooses the model at dispatch for an
unset Model. The Server writes the resolved Runtime explicitly into every new
AgentJob and AgentSession execution snapshot. Runner accepts only the canonical
`pi` and `opencode` values and fails a missing or unknown dispatch Runtime; it
does not own default selection. Existing AgentJobs and Sessions keep their
persisted configuration and are never reinterpreted by a later Agent edit.

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

## Capacity

An Agent runs at most `maxConcurrentRuns` concurrent executions; a non-null
limit is a positive integer, and `null` means unlimited. Capacity is derived
from execution facts in the authoritative stores. No component keeps a permit
ledger, waiter list, or grant notification for capacity: a ledger is a
projection of the facts, a projection must be reconciled against them, and
reconciliation gaps have leaked permits and deadlocked grant delivery
(issue #1078). A derived count cannot drift because its input is the
authority. Runner-level slots remain a separate claim layer owned by the
Runner ([`runner.md`](runner.md#capacity)); a dispatch must satisfy both
bounds, and capacity decisions converge at the Runner claim, per
[the decision record](decisions/one-ledger-no-reconciliation.md).

### Occupancy and identity

An execution occupies one slot for `(project, agent)` from its occupancy claim
until it is terminal:

- a claimed launch Job in `pending`, `running`, or unresolved `unknown`, with
  no terminal timestamp;
- a claimed follow-up Turn in `queued`, `executing`, or unresolved `unknown`,
  in the Session's current context and without supersession.

The initial Turn belongs to its launch Job and never counts a second time.
Unclaimed pending Jobs and queued Turns occupy nothing. Running or executing
status and unresolved `unknown` are themselves evidence of occupancy; a missing
claim timestamp cannot make an unresolved execution disappear from the count.
The timestamp distinguishes claimed but undispatched `pending` or `queued`
work from work that has not claimed capacity. A terminal Job or Turn occupies
nothing, whatever its result. Activity convergence and committed context
supersession end occupancy without inventing a successful result. The enclosing
Session's Activity projection is not a counting filter. Elapsed time cannot
settle uncertainty. The claim timestamp remains on the owner record after
occupancy ends.

Every launch and Session, including Workflow Sessions, retains the accepted
Project and Agent IDs. A later name, Agent edit, or Workflow definition cannot
reattribute its occupancy. The Session's accepted metadata supplies follow-up
identity; the request cannot choose another Agent.

### Atomic claim and owner state

The capacity store has no state of its own. It reads both owner stores and
conditionally claims one owner in one transaction. The transaction obtains
SQLite write serialization before enumeration. It reads the live limit, counts
occupants, checks eligibility and order, and writes the owner claim together.
Counting and claiming in separate transactions is forbidden.

A stored Project Agent's current document supplies its limit, not the accepted
execution snapshot. An exact built-in Agent identity uses its catalog-defined
limit; a built-in needs no stored Project Agent row. An absent or malformed
Project Agent is not an unlimited Agent, and a `builtin:` prefix alone is not
a catalog identity. A limit change and a claim observe one serialized order.
Lowering the limit does not cancel work that already holds a claim.

Missing owner identity or an absent or malformed Agent definition prevents a
new claim. Already accepted work remains `pending` or `queued`, keeps its
original identities, and reports `dispatch-pending` with incomplete capacity
evidence until the definition or identity is repaired. It is not rejected,
expired, or mislabeled `capacity-full`. Reads must not invent zero occupancy or
an unlimited limit from that missing evidence. Existing valid claims remain
occupied and idempotently readable even when the Agent definition is unavailable.

Only the work owner requests its claim. A Job claim checks and advances the
existing ledger revision and its direct API projection in the same transaction.
A Session claim runs inside its serialized grain turn. The owner first flushes
pending Session state and events. The store patches the latest persisted
Session document using an exact-document compare-and-set, then returns the
complete committed document for the owner to install before another save.
Persistence timers cannot interleave a stale whole-document write with this
boundary. No coordinator or other grain writes a Session claim on its behalf.

The Session remains a single-writer aggregate. Exact-document comparison and
cache replacement protect this local claim without adding a separate persistent
Session revision to every save path. A second Session writer would require a
new write-concurrency design; it is not permitted by this boundary.

A failed pre-claim flush writes no claim. A revision conflict or storage
contention requires retry from fresh state, not a false `capacity-full`
conclusion. An uncertain commit requires owner reload or activation quarantine
before another save or dispatch. Retrying an existing valid claim returns the
same owner fact and does not consume another slot.

A pending Job's first claim also fixes its `ReadySince`. Waiting for a Runner,
assignment changes, and re-evaluation preserve that timestamp. The existing
pending-work bound applies even when no Runner is assigned. This bounds
claimed but undispatched work, not an unresolved execution after dispatch.
Runner process and slot claims remain separate from this Agent occupancy claim.

### Queue order and recovery

Capacity pressure never discards accepted work. Only a full input queue rejects
before acceptance. A queued follow-up retains its accepted Input, Turn, and
dispatch identity for as long as it waits; its delivery record cannot expire
because of a lease-duration timer.

Admission follows acceptance order among currently eligible heads. A pending
launch can claim before Runner selection, but a prepared launch that is not yet
visible cannot claim or hold a place ahead of visible work. Only the first queued
follow-up Turn in a Session can compete, and only when that Session's
execution-ownership and operation fences allow dispatch. A queued launch Job
for an existing Session is eligible only when its own initial Turn is the
first locally deliverable Turn. A later queued Turn does not block an earlier
Turn merely because the later Turn belongs to a Job; an earlier ordinary
follow-up holds back the later Job. Execution ownership, uncertain results,
Stop, Reset, and binding fences still apply in both orders. A head blocked by
its own Session does not block eligible work in other Sessions. Capacity never overrides those fences.
The acceptance key is Job `SubmittedAt` or follow-up Turn `RecordedAt`, both
compared in UTC. A Turn retains the time at which its first Input was accepted;
joining another Input does not replace it. For equal times, order Jobs before
Turns, then owner IDs in ordinal order: Job ID for a Job, Session ID for a Turn.
Remaining Turn ties use Turn sequence and then Turn ID in ordinal order.
There is no separate global queue sequence. Unlimited capacity still preserves
Session order and execution fences but needs no cross-Session capacity wait.

The specialized inspection-only Manager recovery Turn remains eligible from
unconfirmed Activity `unknown` under its existing recovery fences. The original
unknown Job dispatch remains withheld, and no confirmed execution owner or
competing Session operation may be bypassed. This recovery never resubmits or
settles the original uncertain Input. Its Turn claims its own capacity while
the original unresolved Job continues to occupy its slot; a finite limit still
applies to both facts.

A waiting launch Job re-evaluates on its existing per-Job recovery reminder.
A Session recovery reminder remains registered while any current,
nonsuperseded follow-up Turn is queued, whether claimed or unclaimed. It wakes
the existing dispatcher; it does not deliver a grant callback. In particular,
a crash after claim but before dispatch must not strand a claimed queued Turn.
The wake is durable before queue acceptance is committed; an orphan reminder
without queued work is harmless and is removed. Activation restores reminder
reachability, and a reminder stops only when no such queued work remains.
A live limit increase or change to unlimited takes effect at the next evaluation.

Availability projections and waiting-work lists (`capacity-full`,
`concurrency-limit`, `dispatch-pending`) read the same occupancy and queued-owner
facts. They preserve the existing reason vocabulary. A launch coordinator
never awaits a capacity decision.

AgentJob references the first Input and Turn created by launch. A completed
AgentJob means that the launch work returned successfully. It does not close the
AgentSession or establish that the user's broader task is complete. Later
Follow-ups never reopen or rewrite that AgentJob.

Agent launch fixes Instructions, Runtime, Model, Variant, Skills, and Workspace
identity for the Session. Later input uses the same execution snapshot. Changes
to these execution settings affect later launches only. The entry point resolves a named Workspace
from its Origin and persists that identity before acceptance. Where an entry
point permits a Workspace override, the caller supplies its name, never a raw
path or Runner default. The Runner may provision the Workspace Home later.
Workspace resolution and provisioning are authoritative in
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
  Turn ID, and acceptance `ContextGeneration`. It never moves to another Turn
  or changes that acceptance generation.
- A Turn can own multiple steer Inputs. A new-turn Input creates a distinct Turn.
  Its `ContextGeneration` identifies the execution context. It is fixed once
  Runtime submission begins; only [pre-submission missing recovery](#pre-submission-recovery)
  may retarget a queued Turn before that boundary.
- The capacity decision precedes acceptance: capacity pressure queues work,
  and only a full queue rejects before acceptance. Accepted Input cannot be
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
                   +------+                work settles / converged
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
`nextAction`; they do not overwrite current Activity. Session detail reads
retain each superseded Turn's identity, status, execution generation, and
supersession time. Input observations retain their acceptance generation.
`nextAction=inspect_previous_execution` directs the operator to those retained
facts; it does not block new work in a safely settled current context.

`admission=ready` requires current Activity `idle`, terminal Turns, no unresolved
external side effect, and no ActiveOperation. Otherwise admission is `blocked`
with a stable reason and next action. New Turn, Compact, Reset, and automatic
missing recovery use this result instead of deriving safety from history.

A steer on a known running Turn is the only ordinary Input exception. It requires
Runtime support, the same complete Binding, and no competing operation. It never
converts `unknown` into safe idle.

#### Activity convergence

`unknown` Activity converges through lifecycle evidence from the owning Runner,
never through elapsed time. Elapsed time cannot distinguish a lost signal from
a lost execution; guessing violates the `unknown` contract above. Two
authorities convert `unknown`:

- Runner re-registration. When a Runner's control connection is re-established,
  Server probes the current binding of every Session with `unknown` Activity
  bound to that Runner. Probes follow the request rules of
  [`runner-transport.md`](runner-transport.md). The probe and its answer carry
  the complete Binding tuple and binding epoch; an answer for a non-current
  binding is discarded under the late-event rule. The Runner answers per
  binding: `executing`, `idle`, or `unknown-to-runner`.
- Runner removal. The operator removes a Runner's execution authority through
  `mo runner revoke`. Credential revocation and fencing of its current control
  connection precede settlement. Sessions bound to that authority with current
  nonterminal or unresolved `unknown` facts settle as `unknown-to-runner`.
  Ordinary unregister, disconnection, and presence expiry are not removal
  evidence. Revocation supersedes execution ownership; it does not prove that
  an external process or side effect stopped.

An `idle` or `unknown-to-runner` answer settles only the generation, Inputs,
Turns, and operation identities captured before the probe. A later accepted
Input or changed operation invalidates that observation even if the binding
and generation are unchanged. Settlement supersedes, after the force-reset
pattern: it marks every captured in-flight or unresolved `unknown` Turn of that
generation terminal `unknown` and every
queued undispatched Turn `cancelled`, records them through
`unresolvedPrevious` and `nextAction`, supersedes any ActiveOperation of that
generation, and settles the launch Job that owns a settled initial Turn.
Superseded facts take no part in current Activity, occupancy, or admission
derivation: they are recorded through `unresolvedPrevious` and `nextAction`
and do not count as unresolved external side effects for `admission=ready`.

- `executing` sets Activity to `active`; the Runner owns the pending turn
  report as before.
- `idle` and `unknown-to-runner` set Activity to `idle`. `unknown-to-runner`
  additionally records the write-side binding fact `unknown-to-runner` as
  deterministic missing evidence. The next accepted Input replaces the binding
  on the same Runner once that identity has execution authority and is available.
  It selects the configured fallback only when the policy in
  [`runtime-switch-context.md`](runtime-switch-context.md) applies; otherwise
  [Runtime Session missing recovery](#runtime-session-missing-recovery) uses
  the recorded evidence on the same Runtime. While the bound Runner is removed
  or unavailable, execution waits. Convergence never selects another Runner or
  performs an implicit handoff.
- A failed or unanswered probe leaves Activity `unknown`.

Settlement never re-executes a Turn, never re-sends a reply, and never replays
Transcript; outbound idempotency is the dispatch identity already carried by
the reply anchor. Convergence is idempotent: repeated probes settle the same
facts. Settlement is a Session transition and competes with other Session
operations under the same fences; it never substitutes for `admission`
evaluation.

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

### Usage and cost

Tokens and money are independent observations. A Runtime that reports Tokens
without a cost does not report a monetary zero; the amount stays unknown.

- A cost is known only when the Runtime reported an amount. An explicitly
  reported zero is a known amount and one cost observation.
- A provider message reports its cumulative cost for that message. The Runner
  emits the non-negative difference from the message's last known amount. An
  omitted amount neither clears that baseline nor emits an observation. A
  repeated amount adds no charge, and a lower repeat is not a refund: it does
  not lower the baseline or make a later repeat count again.
- Server adds reported differences to the Session usage. It never invents an
  amount for a Session that reported none.
- Reporting sums known amounts as recorded cost and counts Sessions with a
  known amount as cost samples, including explicit zeros. A total, current-day,
  windowed, time-series, or derived figure with no known amount stays unknown;
  an unknown time bucket is a gap, not a zero-height observation.
- A derived per-Issue figure needs a known numerator and a positive denominator.
  It keeps the existing completed-Issue cohort, time attribution, currency
  policy, and zero-denominator handling.
- Recorded cost describes what Sessions reported. It is not complete
  consumption, a provider invoice, or a price estimate.
- An amount stored before this contract is not reclassified: an earlier absence
  cannot be reconstructed from an ambiguous numeric record.

## Follow-up and Stop

Follow-up has two paths chosen by current state:

- Idle and ready admission creates one Input and one new Turn.
- A running Turn with Runtime steer support accepts one Input on that Turn when
  no operation competes.
- A running Turn without steer support queues a later Turn in Session order
  unless the queue is full.
- Ordinary `outcome_pending`, `unknown`, or an active context operation rejects
  the request without guessing a target Turn. The specialized inspection-only
  Manager recovery transition is the explicit `unknown` exception described
  in [capacity recovery](#queue-order-and-recovery); ordinary callers cannot
  use it to bypass execution ownership.

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

The durable Workflow launch scope is `(projectId, workflowRunId, stage,
actionAttemptId, workId)`. The handoff grain, invocation, AgentJob, initial Input,
and initial Turn derive from that same scope, and the launch fingerprint includes
the scope before rendered command content. Stage is required because task and
work identifiers are local to a Stage and may repeat in another Stage, including
approval-feedback tasks. Retries of one scope reuse its identities; a different
Stage always creates a different invocation, Job, Input, and Turn even when its
task identifier and rendered prompt match.

A named Session continuation uses the Stage-scoped invocation ID as its
idempotency key. Replaying the same launch therefore returns the same Input and
Turn, while the same task or work ID in another Stage appends a distinct
Job-owned Input and Turn to the shared Session. The later Workflow invocation
retains its own Job as execution and result owner; it is not dispatched as an
ordinary Session-owned follow-up. Because Workflow pre-mints that Turn identity,
the continuation requests a distinct queued Turn instead of coalescing with
another pending input. Each Job's initial provider submission admits only its
own Input and Turn; an earlier Job's unresolved initial submission cannot be
replaced by the next invocation.

New launches write only the Stage-scoped handoff. During activation recovery,
an already-running attempt first reads that key and, only when it has no plan,
reads the exact pre-Stage key so work accepted before the identity change can
finish. The recovered plan keeps its original invocation and fingerprint; the
legacy key is never used to prepare new work.

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

Automatic same-Runtime recovery requires deterministic missing evidence:
the same Runner reports it, or an `unknown-to-runner` binding fact from Activity
convergence stands on its own. Configured fallback to another Runtime follows
[`Runtime Switch Context`](runtime-switch-context.md); it is an authorized
Runtime change and does not establish that the old physical Session is absent.
Both forms of replacement require an idle Session with ready admission, or the
single queued Turn allowed by [pre-submission recovery](#pre-submission-recovery).
No Turn may be running or `outcome_pending`, and no Input, dispatch, Runtime
effect, or operation may be `unknown` beyond the superseded facts convergence
itself recorded.

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

When recovery is unsafe, Mohist retains the original Binding and Turn and sets
`admission=blocked`. Diagnostics retain the execution identity and uncertainty
so an operator can inspect the required Runtime evidence. Mohist must not infer
missing, select another Runner, or replay Transcript.

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
Input not yet submitted, an idle Follow-up, and the queued Follow-up described
below. It is rejected during an
executing Follow-up, for Compact, for a Stop target, and for ordinary Reset.
Reset requires safe admission. `unknown` resolves through Activity convergence
or explicit force-reset; nothing else may clear it.

Recovery never reconstructs Runtime context from Transcript. Transcript is an
audit and presentation record, not a command source.

### Pre-submission recovery

A Runtime can become unavailable after Mohist accepts an Input but before the
Runner submits it. Acceptance records which context received the intent; the
Turn records which context executes it. Keeping those facts separate preserves
accepted work when the physical Session must be replaced.

Only the initial AgentJob Turn proved not yet submitted, or the single queued
Follow-up named by its sealed dispatch, may use this recovery path. The owning
dispatch must identify the sole queued Turn and its accepted payload. Before
submitting any Input, the Runner must establish deterministic missing evidence
or the Runtime readiness condition for configured fallback defined in
[`Runtime Switch Context`](runtime-switch-context.md). The complete expected
Binding, Turn and dispatch must still match; another queued Turn, an executing
or uncertain Turn, an active stop, or an uncertain Session rejects replacement.

Initial AgentJob recovery is advanced by the current AgentJob owner. Its durable
operation record is part of the existing Job ledger and matches the exact
Running claim: Job, work, claimed Runner process generation, Runner, Session,
initial Input, initial Turn, expected Binding, immutable dispatch, and one closed
reason: `same-runtime-missing` or `configured-fallback`. The first reason requires
provider-confirmed missing evidence and keeps the Runtime. The second is allowed
only for a non-Manager OpenCode execution moving to ready Pi on the same
initialized Runner; it never rewrites the frozen execution definition or Model.
An absent, unknown, or changed reason conflicts with the durable operation.
Before each prepare, complete, or start mutation, the route requires the matching
current Runner process generation to remain Online and non-draining. The Job
calls AgentSession to commit or query the matching Binding/Turn operation
receipt; AgentSession never calls back into the Job or Runner. A recovery in
progress refuses old-target reports. This one-way owner call prevents a
Job-to-Session-to-Job wait cycle while making a crash between the two owner
writes resumable under the same operation identity.

The recovery operation is persisted before candidate creation. Runtime adapters
that cannot query candidate creation by that identity make a lost or uncertain
creation result terminally uncertain for this operation: neither a replacement
candidate nor provider Input may be recreated blindly. Once a concrete candidate
is known, the Job persists it before asking AgentSession to adopt it. A lost
AgentSession or Job response is resolved by querying the same operation and
candidate, never by creating another physical Session.

The replacement Binding, context boundary and that Turn's execution generation
commit atomically. Its pending dispatch and any Workflow execution binding target
the replacement. Input acceptance generations, Input and Turn IDs, operation and
delivery identities, payload and occupancy claim remain unchanged. A stale or
failed commit authorizes no submission to the candidate.

Before the provider can receive the initial Input, AgentSession durably admits
that exact effect under the same Job/work/process/Binding fence and marks the
initial Turn executing; only then does AgentJob durably record the matching start
receipt and return submission authority. Replays revalidate the current Binding,
generation, and nonterminal unsuperseded Turn. Once recovery is admitted, every
late result status must carry the complete original Session/Turn identity and the
current physical Runtime/runtimeSessionId through ready, started, and terminal
replay; an old target or missing binding cannot settle the Job. Ordinary
first-binding failures before recovery keep their existing reporting behavior.
A receipt is query evidence, not a renewable execution permit: only the original
live executor that changed the receipt from unstarted to started may submit. An
already-started re-entry or a new process observes the receipt but cannot replay
provider Input. The Runner therefore submits the original accepted payload once
and creates no new Input or Turn.

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
and cannot reconstruct or display the Workspace Home path.

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
  is the target identity and caller-supplied Workspace Home paths are outside
  the target contract.
- Stop recovery still has divergent request and recovery paths, including a
  synchronous Session-to-AgentJob stop-unknown cycle and no deadline on recovery
  redelivery. The one-way, single-owner, deadline-bounded rules above are the
  target.
- Capacity is still brokered by a permit grain with a waiter list and grant
  notifications; the derived occupancy claim above is the target, not the
  shipped system (issue #1078).
- Reconnection settles no `unknown` Activity and force-reset has no
  implementation; today a Runner-reported terminal activity event or the
  Manager recovery turn is the only way `unknown` clears. The lifecycle
  convergence rules above are the target.
- Every Follow-up requires a caller `requestId`. Compact, Reset, recovery,
  handoff, rebind, and force-reset require a caller `operationId`. Some current
  entry points still synthesize a hidden key when the caller omits one, so
  response-loss retry cannot name the original intent.
