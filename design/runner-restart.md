# Runner Restart and Report Settlement

Execution facts are born at the Runner, the only witness present when they
become true. Between the moment a fact becomes true and the moment the Server
records it, a Runner crash can lose it. Preserving every execution fact across
a crash requires a second durable authority on the Runner, and keeping that
authority consistent with the Server costs more than the facts are worth: the
local recovery stack (result journals, event outbox, convergence loops, session
adoption, receipt identities) has become the primary source of settlement
defects. This document defines the target design. The Runner keeps no durable
state, a crash is an ordinary failure, and flow progress comes from the
Workflow's existing failure semantics rather than from reconstructed truth.

## Design Drivers

- A fact must have at least one durable witness. Dispatch facts live in the
  Server's persisted ledgers. Execution facts are witnessed by the Runner
  alone. The chosen trade-off accepts losing in-flight execution facts on
  crash instead of making the Runner durable.
- Blind re-execution after a restart must remain impossible. An Agent turn is
  not idempotent: it spends quota, is nondeterministic, and its side effects
  (commits, pushes) stay in the Workspace. This is the one guarantee the crash
  path must keep.
- Recovery accuracy already exists one layer up. The Workspace on disk, the
  Runtime's session files, and the Server's binding and workflow state all
  survive a Runner crash. A retry attempt is executed by an agent that reads
  that scene; this recovers more truth, more cheaply, than any Runner-side
  reconstruction.
- The Workflow already owns failure semantics: recovery handlers, budgets,
  retry, blocked, manual decision. A crash must enter this path unchanged. No
  crash-specific machinery is justified.
- Rejected alternatives that may return:
  - Result preservation through Runtime session adoption, asking the Runtime
    for a finished turn's outcome. It covers only Runtime-backed Agent turns,
    cannot answer whether the Server already recorded a report, and rebuilds
    execution identity from foreign file formats. Inferring unrecorded state
    is a defect (see [`conventions.md`](conventions.md)).
  - A durable report journal on the Runner. It preserves completed results
    across a crash but turns the Runner into a second durable authority; every
    crash path then becomes a consistency problem between two authorities.
  - Waiting for predecessor delivery before admitting a follow-up turn. The
    Server must decide admission from recorded state alone; it must not wait
    for facts held only by the Runner.

## Model

**Process Generation**. An opaque nonce created at every Runner process start.
The Runner sends it at register and in every poll. Equality, not ordering, is
the contract: two processes under one Runner identity must never share a
generation. The Server records the claiming generation with every claim.

**Report**. A Runner's assertion of an execution fact: a work result, a runtime
event, or a task-log batch. A report is a claim in the sense of
[`conventions.md`](conventions.md): it crosses an edge and is settled, never
trusted. Report identity is (owner, work, attempt). Settlement is idempotent
by that identity.

**Verdict**. The Server's answer to a report:

```text literal
accepted      the fact is recorded; a duplicate report gets the same verdict
refused       the report can never be recorded; its work no longer exists in
              a reportable state
outstanding   the Server cannot decide now; the Runner retries
```

The Server must always produce a verdict. It must not express arbitration
results as transport errors, and the Runner must not interpret status codes.

**Runner state**. The Runner holds no durable state. Everything it needs is
configuration (its identity), rebuildable (Workspace materializations), or
volatile (in-flight work, unacknowledged reports).

Invariants:

```text literal
Work claimed by one generation is never redelivered for execution to another
generation; it is settled as interrupted.

Every Running work item reaches a terminal state without Runner memory:
report verdict, generation closeout, presence-expiry closeout, or deadline
settlement.

A report is retried until a verdict while its process lives.
Process death ends all retries and loses all unrecorded facts.
```

```text diagram
Runner (volatile)                  Server (durable)
  execute, report, retry  ------->  settle: accepted | refused | outstanding
  die                       ------->  closeout: runner-restarted | runner-lost
  register(new generation)  ------->  advance: workflow recovery semantics
```

## Semantics

### Register and Generation Closeout

Register establishes a new process generation. Before the new process's first
poll is served, the Server settles every Running work item assigned to this
Runner under an older generation as failed with reason `runner-restarted`.
This is presence-expiry closeout (`runner-lost`) moved to the earliest
provable moment. Generation closeout is immediate; presence-expiry closeout
remains the backstop for a process that never returns.

The Runner must scope every execution to a per-work process group. On startup,
before claiming work, the Runner must terminate stale groups left by earlier
processes under the same Runner root. A crashed process cannot kill its own
tree, so the sweep makes the old execution dead instead of assuming it.

`runner-restarted` is an ordinary failure code. The Workflow's recovery
declarations decide what follows: handler tasks, `retrySelf`, budget
exhaustion to blocked, or a manual retry. No crash-specific state, route, or
component exists.

### Report

While its process lives, the Runner retries every unacknowledged report in
memory at a fixed cadence until a verdict arrives. `accepted` and `refused`
retire the report. `outstanding` and transport failure keep it.

A report lost to process death is never replayed. Its work is settled by
generation closeout or presence-expiry closeout.

Settlement is idempotent by report identity. A duplicate report, a late report
from a closed-out process, and a report for terminal work all receive a
definite verdict. Refused reports drain naturally.

### Ordering and Admission

Reports belonging to one Session are delivered in emission order while the
process lives. A crash may drop a suffix; the Server must tolerate gaps. The
Server must not wait for Runner-held facts before admitting later work:
admission decisions use recorded state only. A fact whose loss would block
progress must be a fact the Server can settle by lease or deadline.

### Commands

Session commands (stop, compact, reset, follow-up operations) keep no
Runner-side journals. Stop settles by identity: the verdict records only what
the Runtime confirms about the target Turn. Every other command must be
idempotent under its `operationId`; after a crash, the Server retries an
unsettled operation under the same identity.

### Evidence

Runtime events and task logs are evidence, never state (see
[`conventions.md`](conventions.md)). They are retried in memory while the
process lives and may be lost on crash. Loss degrades the timeline and the
audit trail; it must not block state transitions.

### Deadline Settlement

Any work item that can be settled neither by report nor by closeout settles at
a deadline as failed or unknown. Silence is made decidable by leases: an
expiry is a fact, not a guess about the peer (see
[`conventions.md`](conventions.md)).

## Examples

Crash mid-execution:

```text literal
gen1 claims X, starts executing, process dies
gen2 registers
  -> Server settles X: failed, runner-restarted (before gen2's first poll)
  -> Workflow recovery declares a retry -> new attempt X'
  -> gen2 claims X'; the agent reads the Workspace (previous commits
     are present) and continues from the scene
```

Completed but unreported:

```text literal
gen1 finishes X (result R in memory), process dies before the report
gen2 registers -> X settled runner-restarted; R is discarded
The retry attempt re-executes. Side effects of the first execution remain
in the Workspace and are visible to the retrying agent.
```

Ack lost, process alive:

```text literal
report(X, R) -> Server records R -> ack lost in transport
Runner retries from memory -> Server settles by identity -> accepted (again)
Runner retires the report. Exactly one state change occurred.
```

Partitioned process outlives its closeout:

```text literal
gen1 is partitioned; presence expires; X is closed out runner-lost
X is retried as X' on any live Runner
gen1 finishes X and reports -> verdict: refused -> gen1 discards the result
```

## Status

This is the target design; current code does not match it. To be removed: the
durable work-result journal and started-fence recovery, the runtime-event
outbox (ack policies, refusal dead-lettering), the terminal task-log delivery
store, the follow-up/session-command/cancel journals, binding convergence and
recovery coordination, recovered-started-work replay, recovery receipts, and
cleanup-turn admission waits. Claims do not yet record the claiming
generation, and register does not yet perform closeout. Presence-expiry
closeout, idempotent settlement (`accepted`/`stale`), and deadline settlement
exist.
