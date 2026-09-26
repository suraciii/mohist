# Work Confirmation Design

## Dispatch Protocol: Claim / Pull / Report

```text diagram
                 +--------------------------+
                 | Runner process polls and |
                 |     retries reports      |
                 +-------------+------------+
                               |
                               vpoll / report
 + Server ---------------------------------------------------+
 |                    +-----------------+                    |
 |                    | DispatchService |                    |
 |                    | stateless poll  |                    |
 |                    |   computation   |                    |
 |                    +--------+--------+                    |
 |              +--------------+--------------+              |
 |              vClaimNext                    vTouchPresence |
 |+--------------------------+    +-----------------------+  |
 || Owner ledgers assignment |    | RunnerGrain presence, |  |
 ||      and lifecycle       |    |    slots, closeout    |  |
 |+--------------------------+    +-----------------------+  |
 +-----------------------------------------------------------+
```

### Transport

All work, both Workflow and AgentJob, is pull-only: DispatchService computes it
on poll, and reports go directly to the owner grain. The poll is also the
presence heartbeat (`TouchPresence`). Info travels only through register,
unregister, and heartbeat-repair; a poll never updates it.

The target Server-to-Runner Workspace and Session control transport uses one
outbound WebSocket connection. It does not carry work or replace HTTP poll,
report, or reconciliation. The current Server-to-Runner control transport is
defined in [`runner-transport.md`](../transport/design.md).

### Poll Computation

Each poll recomputes everything from persisted state; DispatchService keeps
no cursor, cache, or ledger. A poll carries the Runner's reported set
(`inFlight` union `awaitingAck`) and its readiness signal, and doubles as
the presence heartbeat.

```text diagram
  +----------------------+
  | poll: reported set + |
  |      readiness       |
  +-----------+----------+
              |
              v
   +---------------------+
   | withhold unresolved |
   |     settlement      |
   +----------+----------+
              |
              v
 +------------------------+
 | redeliver Running work |
 +------------+-----------+
              |
              v
  +-----------------------+
  | mine assigned Pending |
  +-----------+-----------+
              |
              v
+--------------------------+
| claim unassigned Pending |
+--------------------------+
```

A newly delivered dispatch joins the reported set synchronously, before the
next poll, so it can never be mistaken for lost work.

An Agent work item whose owner has accepted an `unknown` result is not a lost
dispatch. Until a same-attempt terminal report settles it or the owner deadline
releases it, the original work remains reserved against the owning Runner's
capacity but every poll withholds it: no fresh execution dispatch is emitted,
even when the Runner does not report the work. The owner deadline releases that
capacity, while a genuinely late report with the same task-attempt, work,
Runner, and execution binding remains
authoritative through ordinary settlement. Neither path admits a replacement
turn for the unresolved attempt.

For `reported - desired`, where the owner has already moved beyond the work,
take no action: the process executes it to completion, receives `refused` for
the report as its acknowledgement, and discards the result.

### Runtime Readiness Signal

Presence alone cannot prove that the runtime a pending work item needs is
ready before the Server claims the item. Every poll therefore carries one
readiness signal per runtime: `ready` (can it accept new work now) plus a
Runner-owned `runtimeGeneration` that fences the observation to the current
runtime instance.

The Server treats a missing, malformed, expired, or `ready=false` signal as
unknown for new claims. It never treats a runtime catalog as a readiness
signal. The signal is not durable work state and cannot settle, replay, or
replace a work result.

Redelivery is separate from admission. Work already reported as `inFlight` or
`awaitingAck` remains owned by that Runner and may be delayed while its
runtime recovers; the Runner must not acquire new work and hide it in an
unbounded deferred queue.

A signal is a claim-time admission fence, not a guarantee that an external
runtime cannot fail immediately after the poll. The Server must not infer
readiness from a successful HTTP poll, presence, heartbeat, model catalog,
runtime session file, or reconnect; these facts have different owners and
lifecycles.

### Claim

`ClaimNextAsync` takes the next Pending work item, including the Workflow stage
lock, marks it Running under the Runner identity, and persists it in one atomic
write. There is no offer phase and no Runner-side preregistration.

```text diagram
                                  complete  +-----------+
                                 +--------->| COMPLETED |
+---------+ claim     +---------+|          +-----------+
| PENDING +---------->| RUNNING ++
+---------+           +---------+|fail      +--------+
                                 +--------->| FAILED |
                                            +--------+
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
[`actions.md`](../../workflow/actions/design.md) and
[`task-dispatch.md`](../../workflow/execution/design.md). They are not dispatcher
concerns.

### Fairness

Stamp `ReadySince` whenever work enters or re-enters Ready. Within a candidate
tier, mix Workflow and AgentJob work by `ORDER BY ReadySince ASC`. This produces
round-robin service with no scheduler state:

```text diagram
+------------------------+
| Ready queue ReadySince |
|          ASC           |<+
+------------+-----------+ |
             |             |
             v             |
  +--------------------+   |
  | serve longest wait |   |
  +----------+---------+   |
             |             |
             v             |
   +-------------------+   |
   | requeue next work |   |
   | ReadySince := now +---+
   +-------------------+
```

The policy is strict FIFO. Any priority between work types must be a
declared policy, not an implicit bias.

### Capacity

`slots` limits concurrent Workflow and AgentJob work on a Runner. Claim is the
only final capacity decision: every claim rechecks live registration and
capacity. Poll admission and the AgentJob precheck are advisory. Lowering
capacity affects later claims and never cancels running work; the Runner adds
no second capacity rule.

The AgentJob precheck provides synchronous backpressure when every live Runner
is full. It does not reserve capacity. If another item claims the capacity
first, the AgentJob remains Pending for the next poll.

### Report

Reports go directly to the owning grain through a stateless translation path:

```text diagram
               +--------+
               | Runner |
               +----+---+
                    |
                    vreport
         +---------------------+
         | API route stateless |
         +----------+----------+
                    |
                    v
       +------------------------+
       | Owner ledger settle by |
       |        identity        |
       +------------+-----------+
         +----------+----------+
         vaccepted / refused   voutstanding
 +---------------+   +-------------------+
 | retire report |   | retry from memory |
 +---------------+   +-------------------+
```

Process death ends retry; the work is closed out.

A report is a Runner's assertion of an execution fact: a claim in the sense of
[`conventions.md`](../../../design/conventions.md#facts-claims-and-settlement). Settlement is
idempotent by report identity (owner, work, attempt). An attempt is one Running
episode from claim to terminal report. Settlement answers one of three
verdicts:

```text literal
accepted      the fact is recorded; a duplicate report gets the same verdict
refused       the report can never be recorded; its work no longer exists in
              a reportable state (late, superseded, or terminal elsewhere)
outstanding   the owner cannot decide now; the Runner retries
```

The owner must always produce a verdict. It must not express arbitration
results as transport errors, and the Runner must not interpret status codes:
`accepted` and `refused` retire a report; anything else keeps it.

The report envelope has one closed status vocabulary. Task and AgentJob work
report `completed`, `failed`, `timeout`, or `unknown`. A checks batch reports
`pass` or `fail`; its rows use the same two check verdicts. There are no
`success`, `ok`, or `succeeded` aliases. Route validation, translation, binding
admission, and owner arbitration must use this same vocabulary and must reject
a status that is invalid for the reported owner shape.

An Agent success is `completed`. It is admissible only with the complete
physical execution binding produced by that turn. A binding-less `failed`,
`timeout`, or `unknown` report remains a legitimate pre-binding observation and
settles through the ordinary unknown/failure path; a binding-less `completed`
report is refused and never converted into an unknown observation. This keeps a
successful execution fact from being retired without its physical witness.

While its process lives, the Runner retries every unacknowledged report from
memory at a fixed interval and continues to include it in poll reports. A
report lost to process death is never replayed; its work is closed out.
See [Restart and Crash Semantics](../presence-and-capacity/design.md#restart-and-crash-semantics).

The owner cannot distinguish who produced a report: the execution process,
whether normally or after a timeout, or RunnerGrain closeout.

A stop does not cancel in-flight execution; its eventual report receives
`refused`.
