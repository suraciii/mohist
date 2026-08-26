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

Presence facts follow one lifecycle rule: persistent fields (`slots`) change
only through the control plane and are never invalidated by traffic; snapshot
fields (`lastSeen`, RunnerInfo) are replaced as a whole on register, poll, or
heartbeat-repair, and cleared on unregister.

A Runner holds no work records. Work records fail the ownership test: the
slot invariant is not owned by the Runner, the running-work set can be derived
by querying both owner stores (`Pending/Running WHERE AssignedRunnerId=R`),
and no Runner behavior accepts a work record. Runtime state reads are derived
the same way — assembled from the owner stores, never stored.

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

Each poll recomputes everything from persisted state; DispatchService keeps
no cursor, cache, or ledger. A poll carries the Runner's reported set
(`inFlight` union `awaitingAck`) and its readiness witness, and doubles as
the presence heartbeat.

```text diagram
dispatch order per poll:
  1. redelivery = Running assigned to me - reported    (repay debts first)
  2. mine       = Pending assigned to me,  ReadySince ASC
  3. claimable  = unassigned Pending,      ReadySince ASC

invariant: a newly delivered dispatch joins the reported
set synchronously, before the next poll — it can never
be mistaken for lost work
```

For `reported - desired`, where the owner has already moved beyond the work,
take no action: the process executes it to completion, receives `refused` for
the report as its acknowledgement, and discards the result.

### Runtime Readiness Witness

Presence alone cannot prove that the runtime a pending work item needs is
ready before the Server claims the item. Every poll therefore carries one
readiness witness per runtime: `ready` (can it accept new work now) plus a
Runner-owned generation that fences the observation to the current runtime
instance.

The Server treats a missing, malformed, stale, or `ready=false` witness as
unknown for new claims. It never treats a runtime catalog as a readiness
witness. The witness is not durable work state and cannot settle, replay, or
replace a work result.

Redelivery is separate from admission. Work already reported as `inFlight` or
`awaitingAck` remains owned by that Runner and may be delayed while its
runtime recovers; the Runner must not acquire new work and hide it in an
unbounded deferred queue.

A witness is a claim-time admission fence, not a guarantee that an external
runtime cannot fail immediately after the poll. The Server must not infer
readiness from a successful HTTP poll, presence, heartbeat, model catalog,
runtime session file, or reconnect; these facts have different owners and
lifecycles.

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
AgentJob combined. Claim is the only final capacity decision: every new claim
rechecks the Runner's live registration and capacity. Everything earlier —
poll admission, the AgentJob precheck — is advisory. Lowering capacity
constrains subsequent claims without cancelling running work; the Runner
process enforces no capacity rule of its own.

The AgentJob precheck exists to give the caller synchronous backpressure when
every live Runner is full. Passing it promises nothing: another work item may
consume capacity between precheck and claim, in which case the job remains
Pending for the next poll. No synchronous capacity promise could be kept
because every decision has a window before actual execution; the two-step
design narrows the promise to one the system can uphold.

### Report

Reports go directly to the owning grain through a stateless translation path:

```text diagram
Runner -> API route -> stateless translation -> owner grain -> verdict
```

A report is a Runner's assertion of an execution fact — a claim in the sense
of [`conventions.md`](conventions.md#facts-claims-and-settlement). Settlement
is idempotent by report identity (owner, work, attempt) and answers one of
three verdicts:

```text literal
accepted      the fact is recorded; a duplicate report gets the same verdict
refused       the report can never be recorded; its work no longer exists in
              a reportable state (late, superseded, or terminal elsewhere)
outstanding   the owner cannot decide now; the Runner retries
```

The owner must always produce a verdict. It must not express arbitration
results as transport errors, and the Runner must not interpret status codes:
`accepted` and `refused` retire a report; anything else keeps it.

```text diagram
Runner --- report --> owner grain -- settle by identity --> accepted: retire
                                                         |-> refused: retire
                                                         |
Runner <--- retry, fixed cadence, from memory ----------- +-> outstanding or
             while the process lives                        no answer

process death: retry ends; closeout settles the work
```

While its process lives, the Runner retries every unacknowledged report from
memory at a fixed interval and continues to include it in poll reports. A
report lost to process death is never replayed; its work is settled by
closeout. See [Restart and Crash Semantics](#restart-and-crash-semantics).

The owner cannot distinguish who produced a report: the execution process,
whether normally or after a timeout, or RunnerGrain closeout.

A stop does not cancel in-flight execution; its eventual report receives
`refused`.

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
- Runner restarts: register establishes a new process generation; the old
  generation's Running work is settled `FAILED("runner-restarted")`. See
  [Restart and Crash Semantics](#restart-and-crash-semantics).
- No Server-side timer times out work. Reported in-flight work is alive; only
  the process judges progress. Owner timers, including AgentJob execution and
  dispatch timeouts, are decided by owner reminders and are unrelated to
  dispatch.

Presence has three refresh paths — register, poll, heartbeat — so a Runner
stays visible while its process lives, even when it cannot complete a poll.
Explicit unregister clears presence and closes out assigned work.

`runner-lost` is a failure reason, not an owner state. The owner marks affected
work failed: WorkflowRun enters its existing `Failed` state and projects the
Issue as `blocked`; AgentJob symmetrically enters its existing `Failed` state.
There is no `Interrupted` state.

## Restart and Crash Semantics

The recovery goal is flow progress, not result preservation. A Runner crash
loses whatever the process alone remembered; the Workflow's existing failure
semantics — recovery handlers, budgets, retry, blocked, manual decision —
decide what follows. A crash is one failure code among others, not a special
event with its own machinery.

Three forces shape this boundary:

- A fact needs at least one durable witness. Dispatch facts live in the
  Server's persisted ledgers. Execution facts are witnessed by the Runner
  alone, and the Runner is volatile by choice: making it durable would create
  a second authority whose consistency with the Server must then be maintained
  forever.
- Blind re-execution after a restart must remain impossible. An Agent turn is
  not idempotent: it spends quota, is nondeterministic, and its side effects
  (commits, pushes) stay in the Workspace. This is the one guarantee the crash
  path must keep.
- Recovery accuracy already exists one layer up. The Workspace on disk, the
  Runtime's session files, and the Server's binding and workflow state all
  survive a Runner crash. A retry attempt's agent reads that scene and
  continues; this recovers more truth, more cheaply, than any Runner-side
  reconstruction.

**Process Generation**. An opaque nonce created at every Runner process start,
sent at register and in every poll. Equality, not ordering, is the contract:
two processes under one Runner identity must never share a generation. The
Server records the claiming generation with every claim.

Register establishes a new generation. Before the new process's first poll is
served, the Server settles every Running work item assigned to this Runner
under an older generation as `FAILED("runner-restarted")`. Work claimed by one
generation is never redelivered for execution to another. This is
presence-expiry closeout moved to the earliest provable moment; `runner-lost`
remains the backstop for a process that never returns.

```text diagram
gen1   claim X -> execute -> (process dies)

gen2   register(gen2)
         Server, before serving gen2's first poll:
           X, Running and claimed by gen1 -> FAILED(runner-restarted)
         workflow recovery -> retry -> new attempt X'
       poll -> claim X' -> execute
         the retrying agent reads the Workspace scene and continues
```

The Runner scopes every execution to a per-work process group. On startup,
before claiming work, the Runner terminates stale groups left by earlier
processes under the same Runner root. A crashed process cannot kill its own
tree, so the sweep makes the old execution dead instead of assuming it.

Rejected alternatives that may return:

- Result preservation through Runtime session adoption: asking the Runtime for
  a finished turn's outcome. It covers only Runtime-backed Agent turns, cannot
  answer whether the Server already recorded a report, and rebuilds execution
  identity from foreign file formats. Inferring unrecorded state is a defect
  ([`conventions.md`](conventions.md#facts-claims-and-settlement)).
- A durable report journal on the Runner: preserves completed results across a
  crash, but turns the Runner into a second durable authority; every crash
  path becomes a consistency problem between two authorities.
- Waiting for predecessor delivery before admitting a follow-up turn: the
  Server must decide admission from recorded state alone; it must not wait
  for facts held only by the Runner.

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

When a Runtime Session quarantines or a Runner shuts down, the Runner drains
in-flight work within bounded time before it releases ownership. Results
produced during drain are reported before ownership is released; a result not
reported before process death is lost with the process, and closeout settles
its work.

## Persisted State

The Runner holds no durable state. Everything it needs is configuration (its
identity), rebuildable (Workspace materializations), or volatile (in-flight
work, unacknowledged reports). Cross-process consistency comes from report
settlement and poll recomputation, not shared files. The Server never reads
or writes Runner-local files.

A fact must have at least one durable witness, and the Runner is not one.
Facts the Runner witnesses are either reported — and the Server becomes the
durable witness — or lost with the process, with closeout settling the work.
A Runner-side journal would only buy result preservation across a crash, a
capability this design deliberately declines (see
[Restart and Crash Semantics](#restart-and-crash-semantics)).

A Runner may keep rebuildable on-disk caches, such as the Workspace
materialization indexes, as long as they are never authoritative and fail
open: a corrupt or missing cache is rebuilt from the filesystem and from
Server answers.

Gap: current code persists eight files under
`<runnerRoot>/.mohist/runner-state/` — a work-result journal with
started-fence replay, a runtime-event outbox, a terminal task-log delivery
store, three operation idempotency logs, and two workspace registries — plus
binding convergence, recovery receipts, and cleanup-turn admission waits
built on top of them. These mechanisms implement the rejected
result-preservation alternative and are to be removed. Only the rebuildable
workspace indexes survive.

### Stop Operations Stay Available and Settle by Identity

Stop must remain available independent of any other Runner state or delivery
health.

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

Session commands such as Compact and Reset must be idempotent under their
`operationId`: after a Runner restart the Runtime cannot answer whether the
effect occurred, so the Server retries the unsettled operation under the same
identity. Stop differs because its intent is checkable — whether the target
Turn still exists — while a Compact or Reset effect is not.

Gap: the current handler accepts one runtime's abort acknowledgment as a
confirmed stop, maps target-resolution failures and absent live targets to
not-cancellable, returns redelivery verdicts without recording them, and
leaves a corrupt journal permanently unavailable. Under the certainty
vocabulary these are fabricate and estimate defects
([`conventions.md`](conventions.md#facts-claims-and-settlement)).

