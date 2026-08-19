# Runner and Dispatch

Dispatch is memoryless: every decision is a stateless query over persisted
state. A Runner's self-report is used only to discover work that needs
redelivery. It is not authoritative; authority is always reconstructed from the
current store contents.

Every fact has exactly one owner:

```text diagram
What work was dispatched to whom -> WorkflowRun / AgentJob
                                    (each is its own queryable dispatch ledger)
What is executing right now      -> Runner process memory, reported on each poll
Whether a Runner is alive        -> RunnerGrain.lastSeen
```

There is no second copy. No component stages dispatch or work state owned by
another owner; it reconstructs that state from the owner's persisted state when
needed. Dispatch coordination therefore needs no reconciliation because there
is no duplicate fact to reconcile.

Invariants:

```text literal
Every WorkflowRun / AgentJob is its own dispatch ledger.
Running => corrected within one poll as:
           reported | re-dispatched | rejected as invalid | closed out
count(Running work assigned to Runner) <= slots, checked at claim time
```

## Runner Aggregate and Presence

Fields are grouped by update lifecycle, never by who reports them. Persistent
fields change only through the control plane, a few individual fields at a
time, and are never invalidated. Snapshot fields are replaced as a whole on
register, successful poll, or unregister, and the next successful poll
invalidates them.

```text literal
Runner
  runnerId                       identity
  slots                          persistent; owned by the control plane
  lastSeen                       snapshot; established at register, renewed by successful poll
  info: RunnerInfo|null          populated by register, refreshed by heartbeat-repair,
                                  cleared by unregister

RunnerInfo
  state: online|offline          established by register, maintained by poll freshness
  hostname, buildGitHash
  capabilities, coderModels, coderModelVariants
```

A Runner holds no work records. The two work types remain authoritative in
their owners' stores: Workflow work in the run row model and AgentJob work in
the AgentJob dispatch projection. Both can be queried directly as
`Pending/Running WHERE AssignedRunnerId=R`. The slot invariant
(`count(running) <= slots`) is checked against the store during claim and is
not maintained here. Work records fail the ownership test on the Runner: the
slot invariant is not owned by the Runner itself, the running-work set can be
derived by querying both owner stores, and no Runner behavior accepts a work
record.

### Behaviors

```text literal
Register(info)          state=online, lastSeen=now, populate info, write registry
Unregister()            state=offline, clear info;
                        close out by reporting FAILED("runner-lost") to owners
TouchPresence()         successful poll or heartbeat: lastSeen=now; restore online registry state
HeartbeatRepair(info)   refresh info and presence atomically; keep the same gate
Update(slots)           write-through
```

Each behavior touches one lifecycle group.

### Runtime Read

`GetRuntimeStateAsync()` returns `RunnerRuntimeState` containing status,
lastSeen, and activeWorks.

activeWorks is a direct union of reads from the two owner stores:

- Workflow: `Running assigned to me`, with the current task and checks for each
  run.
- AgentJob: `Pending/Running assigned to me` from the AgentJob dispatch
  projection.

## Dispatch Protocol: Claim / Pull / Report

```text diagram
WorkflowGrain / WorkflowRun       workflow work dispatch ledger
  owns Assignment and lifecycle: Pending / Running / terminal
  ClaimNext: atomic Pending -> Running plus stage lock
  consumes reports idempotently: report for terminal work -> Stale
  has no timer and no Runner concept

AgentJobGrain / AgentJob          AgentJob work dispatch ledger
  owns work state and the sole DispatchSnapshot
  admission: eligibility precheck selects a Runner, then one transaction writes
    AssignedRunnerId + ReadySince + DispatchSnapshot; no other component call
  ClaimNext: atomic Pending -> Running; idempotent
  consumes reports idempotently: report for terminal work -> Stale
  reminder does one thing:
    Pending with an old ReadySince -> Failed(RunnerUnavailable)

RunnerGrain
  owns presence, slots, and closeout
  holds no work records

DispatchService                  stateless; not a grain
  each poll: desired - reported -> dispatches
  reads everything from persisted state; no cursor, cache, or ledger

Runner process                   physical process
  one process-level critical loop owns polling and report retry
  executes work concurrently with progress-aware timeout
  each poll reports the full inFlight + awaitingAck set
  retries due reports at a fixed interval until acknowledged
```

### Transport

All work, both Workflow and AgentJob, is pull-only: DispatchService computes it
on poll, and reports go directly to the owner grain. The poll is also the
presence heartbeat (`TouchPresence`). Info travels only through register,
unregister, and heartbeat-repair; a poll never updates it.

The target Server-to-Runner Workspace and Session control transport uses one
outbound WebSocket connection. It does not carry work or replace HTTP poll,
report, or reconciliation. The SignalR-to-WebSocket migration is authoritative
in [`runner-transport.md`](runner-transport.md).

### Poll Computation

```text diagram
Runner process                  DispatchService                    store / grains
    | POST poll {inFlight, awaitingAck, readiness}                       |
    |---------------------------->|                                      |
    |                             | 0 BeginPoll: capture slots + gate     |
    |                             | 1 TouchPresence (poll = heartbeat)    |
    |                             | 2 desired = Running assigned to me    |
    |                             |   from both owner types               |
    |                             | 3 redelivery = desired - reported     |
    |                             |   rebuild each from owner state       |
    |                             | 4 spare = slots - active work count   |
    |                             |   while spare > 0:                    |
    |                             |     Pending assigned to me, then      |
    |                             |     claimable Pending                 |
    |                             |     each ORDER BY ReadySince ASC      |
    |                             |     ClaimNext ----------------------->| Pending -> Running
    |                             |       ok: build dispatch; spare--     | (+ stage lock)
    |                             |       null: try next candidate        |
    | { dispatches[] }            |                                      |
    |<----------------------------|                                      |
    |                             | EndPoll: release gate                 |
    | inFlight.add(dispatches)    |                                      |
    | execute concurrently        |                                      |
```

Ordering is redelivery first, then Pending assigned to this Runner, then
claimable Pending. Repay existing obligations before expanding.

For `reported - desired`, where the owner has already moved beyond the work,
take no action. The process executes it to completion, receives `Stale` for the
report as its acknowledgement, and discards the result.

Race avoidance: the process adds received dispatches to inFlight synchronously
before the next poll. A newly delivered dispatch can never be mistaken for lost
work.

### Runtime Readiness Witness

Presence alone cannot prove that the runtime a pending work item needs is
ready before the Server claims the item. The Runner therefore sends a runtime
readiness witness in every poll. A witness is an ephemeral observation bound
to the Runner connection and contains:

- `runtime`: the canonical runtime id, for example `pi` or `opencode`;
- `ready`: whether this runtime can accept new work now;
- `generation`: a monotonically increasing runtime instance fence owned by
  the Runner.

The poll boundary binds the witness to the current control connection. Runner
sends its public connection ID with the poll; after matching the current lease,
Server injects the corresponding process-local `connectionGeneration`. The
witness itself does not carry or own that Server fence.

The Server treats a missing, malformed, stale, or `ready=false` witness as
unknown for new claims. It never treats a runtime catalog as a readiness
witness. The witness is not durable work state and cannot settle, replay, or
replace a work result.

For each pending candidate, DispatchService resolves the candidate's runtime
without mutating its owner, applies the witness predicate, and only then calls
the owner claim operation. Workflow runtime resolution is a read-only
projection of the pending `WorkItem`; it must not call `ClaimNextAsync`
merely to discover `uses`. AgentJob runtime resolution uses its persisted
dispatch snapshot. If either projection is unavailable, the candidate remains
pending for a later poll.

Redelivery is separate from admission. Work already reported as `inFlight` or
`awaitingAck` remains owned by that Runner and may be delayed while its
runtime recovers. The Runner continues to poll, report, and acknowledge that
held work; it must not acquire new work and hide it in an unbounded deferred
queue.

A witness is a claim-time admission fence, not a guarantee that an external
runtime cannot fail immediately after the poll. If readiness changes after a
successful claim, the claimed work follows the existing in-flight and
result-uncertain protocol. A later connection or runtime generation cannot
reuse an older witness. The Server must not infer readiness from a successful
HTTP poll, presence, heartbeat, model catalog, runtime session file, or
reconnect; these facts have different owners and lifecycles.

### Claim

`ClaimNextAsync` takes the next Pending work item, including the Workflow stage
lock, marks it Running under the Runner identity, and persists it in one atomic
write. There is no offer phase and no Runner-side preregistration.

```text diagram
PENDING --ClaimNext--> RUNNING --report(success|fail)--> COMPLETED|FAILED
```

A failed claim, from stage-lock contention or changed state, returns null and
moves to the next candidate in the same poll. If claim succeeds but the
dispatch is lost, the work remains Running and unreported, so the next poll
redelivers it.

Dispatch construction has two failure classes. Ordinary failures caused by an
external dependency or mutable configuration leave the work Running for retry
on the next poll. If a persisted WorkItem `uses` a retired Action, the
translator returns an explicit non-retryable rejection. DispatchService then
commands the owner, using `workerId + workId`, to mark that work Failed. The
command must verify the currently active work; a generic "fail current task"
operation could damage newer work after the owner advances. The Runner decides
Action input contract errors, including unknown keys, missing required values,
and wrong types, during post-render manifest validation as specified by
[`actions.md`](workflow/actions.md) and
[`task-dispatch.md`](workflow/task-dispatch.md). They are not dispatcher
concerns.

### Fairness

Stamp `ReadySince` whenever work enters or re-enters Ready. Within a candidate
tier, mix Workflow and AgentJob work by `ORDER BY ReadySince ASC`. This produces
round-robin service with no scheduler state:

```text diagram
work completes -> owner advances -> next work becomes Pending -> ReadySince := now
the just-served item moves to the tail; the longest-waiting item is at the head
```

The policy extension point defaults to strict FIFO. If interactive AgentJobs
must take priority over background Workflows, extend it explicitly to
`Priority DESC, ReadySince ASC`; priority must be a declared policy, not an
implicit bias.

### Capacity

`slots` limits all work executing concurrently on a Runner, Workflow and
AgentJob combined. There is one final capacity decision: every new claim
rechecks the Runner's live registration and capacity under the Runner lifecycle
gate. `BeginPoll` prevents overlapping polls, but its capacity snapshot is only
advisory. Lowering capacity constrains subsequent claims without cancelling
running work. Unregister ordered before a claim rejects the claim; unregister
ordered after a claim closes it out. The process enforces no capacity rule.

Poll admission is an ephemeral, token-fenced lease. `BeginPoll` returns an
opaque token; only `EndPoll` carrying that exact token can release the round.
Dispatch always releases the token in `finally`, including request cancellation
and core failures. A canceled poll stops creating further offers. A durable
claim that completed before cancellation remains Running and is redelivered by
the next poll under the ordinary at-least-once contract.

The AgentJob admission capacity check is a precheck. Runner selection filters
out live Runners already at capacity; when all are full, it rejects
synchronously so the caller sees backpressure immediately. Passing the
precheck does not promise capacity; claim is the final decision. Another work
item may consume capacity between precheck and claim, in which case the job
remains Pending for the next poll. No synchronous capacity promise could be
kept because every decision has a window before actual execution. The two-step
design narrows the promise to one the system can uphold.

### Report

Reports go directly to the owning grain through a stateless translation path:

```text diagram
Runner -> API route -> stateless translation -> owner grain -> Accepted | Stale
                                                                  both acknowledge
```

At-least-once delivery moves completed work to `awaitingAck`, retries the
original result at a fixed interval, and continues to include it in poll
reports. It can never be mistaken for lost work. Both `Accepted` and `Stale`
stop retry.

The owner cannot distinguish who produced a report: the execution process,
whether normally or after a timeout, or RunnerGrain closeout.

## Supervision and Runner-Lost Closeout

Each failure condition has exactly one owner:

- Poll transport unavailable: the Runner process makes a bounded attempt,
  then retries in the same loop.
- Loop exits unexpectedly: the Runner process terminates, and the service
  supervisor restarts it.
- Work hangs or escapes control: the Runner process applies a progress-aware
  timeout, kills the work, and reports FAILED.
- Runner disappears: RunnerGrain lets poll freshness expire, marks the Runner
  offline, queries both owner types for `Running assigned=me`, and reports
  `FAILED("runner-lost")` for each.
- No Server-side timer times out work. Reported in-flight work is alive; only
  the process judges progress. Owner timers, including AgentJob execution and
  dispatch timeouts, are decided by owner reminders and are unrelated to
  dispatch.

Register establishes initial presence and persists the last registration
profile. HTTP heartbeat refreshes presence even when the Runner process cannot
complete a poll; a payload heartbeat also refreshes the persisted info under
the same lifecycle gate. After activation loss, the first successful poll uses
the persisted profile to restore presence and registry state.
Explicit unregister clears the profile. The registry is written only when
state or info changes, never on each poll.

`runner-lost` is a failure reason, not an owner state. The owner marks affected
work failed: WorkflowRun enters its existing `Failed` state and projects the
Issue as `blocked`; AgentJob symmetrically enters its existing `Failed` state.
There is no `Interrupted` state.

### Failure Handling

- Poll transport fails: retry in the same Runner process and retain the
  reported set.
- Dispatch response is lost: the next poll computes `desired - reported` and
  redelivers.
- Process restarts: an empty report causes full redelivery.
- Ordinary dispatch construction failure after claim: the work remains
  Running and retries every poll.
- A persisted WorkItem references a retired Action: reject that work by
  `workerId + workId`; the owner marks it FAILED.
- Runner rendering or manifest validation fails: the attempt fails as
  `invalid-input`; do not redeliver.
- Report transport fails: retry awaitingAck; the report remains reported and
  is never redelivered.
- Duplicate or late report: the owner idempotently returns Stale.
- Work hangs: the process timeout produces FAILED.
- Runner is lost: closeout reports `FAILED("runner-lost")` and the owner
  enters Failed.
- Runner returns after closeout: its report receives Stale; the work is no
  longer desired and drains naturally.
- A run or job is stopped while work executes: do not cancel; the report
  receives Stale.
- An AgentJob has no available Runner for too long: the owner ReadySince
  timeout produces `FAILED(RunnerUnavailable)`.

## Process Contract

The Runner process has exactly one process-level critical loop, which owns poll
cadence and bounded retry of unacknowledged reports. Transport failure does not
end the loop. Unexpected loop exit terminates the process so the service
supervisor can restart it. Auxiliary heartbeat or control-connection loops must
never keep a Runner process alive when it is not polling.

The reported set, `inFlight` union `awaitingAck`, belongs to the process
lifecycle and must survive poll failures and reconnection. Otherwise one
transient poll failure would remove every held work item from the report and
cause a redelivery storm. The same critical loop schedules bounded report
retry; it is not a separate lifecycle loop.

Runner also owns every external command as one process tree. Direct-child
`exit` records the outcome but does not complete the command result because
stdout and stderr pipes may still contain unread bytes. Runner terminates any
remaining members of that process group so inherited pipes cannot stay open,
then completes exactly once at the child `close` boundary after both streams
have drained. Timeout and parent abort use the same tree ownership; no command
may intentionally daemonize descendants through this primitive.

Work lost with a Runner is reported to its owner as
`FAILED("runner-lost")`. The owner decides what follows. There is no
`Interrupted` state.

When a Runtime Session quarantines or a Runner shuts down, the Runner drains
in-flight work before it releases ownership. Two env-var budgets bound the
drain: `QUARANTINE_DRAIN_TIMEOUT_MS` (default 60s) and
`RUNTIME_SHUTDOWN_TIMEOUT_MS` (default 30s). Results produced during drain
are journaled so a restart can settle them exactly once.

## Local Workspace Lifecycle

The Server owns the logical Workspace and its lifecycle. The Runner owns only
its local materialization and is the only component that touches that
filesystem. This split keeps loss of a reconstructible directory from deleting
the durable environment identity or changing which Sessions belong to it. The
identity, Origin, Home, and reclamation rules are authoritative in
[`workspace.md`](workspace.md).

Dispatches that still use the per-WorkflowRun fallback materialize a standalone
partial clone. The clone retains the commit graph and every remote branch ref,
omits tags, and defers blob transfer until checkout so recovery can still find
an existing run branch without transferring unrelated file contents. Clone and
checkout use the same bounded network-command contract. Preparation happens at
a private `.preparing` path that is removed after any failure and becomes the
published Workspace only by atomic rename. Each WorkflowRun has its own clone;
the fallback does not use a shared cache, Git alternates, or an operator
checkout.

The Runner records each materialization in a reconstructible
`NamedWorkspaceRegistry`, keyed by `(ProjectId, WorkspaceName)`. The registry is
a local maintenance index, not a second Workspace store. Every entry has one
phase. `active` means the Runner has not received a current Server grant for
reclamation. `eligible` means the Server reports the Workspace archived or
active with no active bound Session, so disk policy may delete the
materialization. `stuck` means deletion safety checks rejected
deterministically; the Runner retains the directory and index entry and does
not retry automatic deletion.

The Runner periodically asks the Server whether each `active` entry is
reclaimable. Transport failure or an unknown answer keeps it `active`. An
archived Workspace is reclaimable. An active Workspace is reclaimable only
while it has no active bound Session; deletion removes only the local directory,
and later use rematerializes the same logical Workspace. The Runner never
derives this grant from WorkflowRun status.

One Workspace has two independently reclaimable local resources:

- the materialized directory is reclaimed by retention and storage budget;
- a Runtime directory resource is in-process state retained by an external
  Runtime and released as soon as that Runtime's own safety conditions permit.

The resources share only directory identity. Releasing the Runtime directory
resource does not delete the worktree, and deleting the worktree does not
replace Runtime release. Both use the Runner's existing Workspace maintenance
cycle, once every two minutes by default, without a per-Workspace timer or new
user configuration. Each pass is single-flight: if one pass is still running,
the next does not overlap. Periodic maintenance releases Runtime resources
before applying disk policy. Runtime release does not depend on retention,
storage budget, or successful Server configuration reads.

Disk deletion has an additional concurrency constraint. Successful periodic
Runtime reclamation proves only that the resource was released at that moment;
it does not authorize a later disk deletion because a new operation may reuse
the directory in between. Every automatic or manual deletion must reacquire the
directory's Runtime removal fence. Within one exclusive boundary it performs
any required Runtime release, directory deletion, and registry removal. Even
when the Runtime has no record for the directory, a temporary fence blocks new
operations during deletion, but it must not create a Runtime resource merely
to check. If the Runtime reports the directory busy, cannot decide, or cannot
release it, deletion fails explicitly or is deferred. See the OpenCode-specific
conditions in
[`runtimes/opencode.md`](runtimes/opencode.md#directory-instance-reclamation).

## Persisted State

The Runner persists operational state under `<runnerRoot>/.mohist/runner-state/`.
Each file is written atomically with a temporary file plus rename and loaded at
startup. Corruption semantics differ based on whether lost state can be
reconstructed:

- `runtime-events.json`: the Runtime event outbox — Session events pending
  delivery to the Server, with a snapshot written for each new fact. If the
  file is unreadable, the Runner marks the outbox unhealthy and reloads at a
  local retry cadence; it never overwrites the unreadable file. When
  retention is exceeded, the Runner discards reconstructible streaming
  increments first.
- `followup-operations.json`: the Follow-up operation idempotency log,
  operationId -> claimed / submitted, written on each transition. A wrong
  version or shape makes the log unavailable and rejects new operations
  (fail closed); a missing file means a fresh start.
- `session-commands.json`: the Session command idempotency log, operationId
  -> started / completed plus result, with the same write behavior.
  Corruption fails closed; a missing file means a fresh start.
- `cancel-operations.json`: the stop operation idempotency log, operationId
  -> claimed / completed plus verdict, with the same write behavior. If the
  file is unreadable, the Runner quarantines it aside and restarts empty;
  stop verdicts re-settle by identity, so the next redelivery rebuilds the
  lost record.
- `named-workspaces.json`: the current named Workspace materialization
  index — Project, Workspace name, path, phase, and materialization times.
  If the file is unreadable or corrupt, the Runner starts with an empty
  index and rebuilds on later materialization; the Server remains the
  logical authority.
- `workspaces.json`: the legacy per-WorkflowRun materialization index used
  only by the fallback execution path. It has the same fail-open behavior;
  remove it with the fallback rather than treating it as a second identity
  model.

Idempotency logs fail closed because losing them can repeat effects. The stop
journal is the exception: its effect is checkable by identity, so corruption
quarantines the file and the journal restarts empty instead of failing
closed. The registries fail open because they can be reconstructed from disk.
These files are Runner-private. The Server never reads or writes them
directly; cross-process consistency comes from event delivery and poll
recomputation, not shared files.

### Stop Operations Stay Available and Settle by Identity

Stop is the one Runner operation that must remain available while the event
outbox snapshot is being recovered; its journal never gates on outbox health.

Stop settlement has exactly one witness for the effect: the Runtime that owns
the target Turn. A stop verdict records only what that witness confirms:

- The settled verdict is ended when the target Turn no longer exists, idle
  when the target Session exists with no Turn in flight, and stop-requested
  while the witness has not yet confirmed the effect. Requesting an abort is
  a claim; only the Runtime's confirmation settles it.
- A stop that cannot reach its witness stays unavailable. The witness's
  not-cancellable answer means the Turn is still executing; it is recorded
  honestly and never rewritten. A failure while resolving the target is an
  unknown, never a not-cancellable verdict. A target that provably has no
  live Session settles ended by identity rather than reporting
  not-cancellable.
- A redelivered stop under an already-claimed operationId settles by identity
  first: it probes the target Turn and records the settled verdict. An
  outcome the witness cannot settle — stop-requested or unavailable — leaves
  the claim outstanding and is never recorded as a verdict. Once a verdict
  is recorded, every later redelivery returns it unchanged.

Session commands such as Compact and Reset fail closed on an uncertain start
instead: after a Runner restart the Runtime cannot answer whether the effect
occurred, so the original operation stays unavailable and Server retries it
under the same identity. Stop differs because its intent is checkable —
whether the target Turn still exists — while a Compact or Reset effect is not.

Gap: the current handler accepts one runtime's abort acknowledgment as a
confirmed stop, maps target-resolution failures and absent live targets to
not-cancellable, returns redelivery verdicts without recording them, and
leaves a corrupt journal permanently unavailable. Under the certainty
vocabulary these are fabricate and estimate defects
([`conventions.md`](conventions.md#facts-claims-and-settlement)).

The per-WorkflowRun Workspace manager and `workspaces.json` registry remain an
implementation gap for dispatches that still lack a named Workspace. New code
must not extend that fallback. Removing it does not require a compatibility
model because Runner materializations are reconstructible.

## Decision Record: One Ledger, No Reconciliation

AgentJob work was previously delivered over a push channel. AgentJobGrain
pushed DispatchSnapshot across grains into staging in the Runner aggregate,
which persisted a second work record. Periodic reconciliation then compared
the staged record with its owner. That form violated the opening invariant of
no duplicate copy. Reconciliation was not a design feature; it was the carrying
cost of redundant state, together with cross-grain callback cycles, races
between assignment and poll, and ledger hydration on activation.

The unified design makes AgentJob, like WorkflowRun, its own dispatch ledger.
Dispatch fields (`Status`, `AssignedRunnerId`, `ReadySince`, and
`DispatchSnapshot`) are persisted in a queryable projection. DispatchService
computes desired work identically for both owner types, and the owner completes
claim atomically. The Runner aggregate returns to presence, slots, and closeout
without work records. The cross-grain cycle of assignment callbacks and
runnable reverse lookups disappears, and capacity decisions converge at claim.
The old push channel's Runner-side staging, reconciliation loop, and dispatch
retry state machine (`DispatchAttempts`, retry bound, and acceptance fence) are
deleted together. The owner handles the case of an AgentJob with no available
Runner through its own ReadySince timeout.
