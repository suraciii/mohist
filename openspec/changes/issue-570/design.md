# Design: Durable Runner Work-Result Delivery

## State Rules

Each Runner work identity has one local journal entry:

- `started`: the exact dispatch was admitted, but no authoritative result was
  durably recorded. A later process must refuse that dispatch.
- `completed`: the result is durably recorded and may be reported repeatedly
  with the same work identity until the server returns a durable acknowledgement.
- absent: the identity is not held by this process and may be admitted when the
  server dispatches it.

The journal uses a temporary file followed by rename. Corrupt or unreadable
state, and any failed journal write, make the journal unavailable and gate new
claims. A failed completion leaves the work in the process reported set and
does not report the result. A failed acknowledgement keeps the completed
entry and awaiting-ack work, so an accepted result can be replayed safely.

## Recovery Sequence

1. Load the journal before connecting and claiming work.
2. Put completed entries into the existing `awaitingAck` set with an immediate
   bounded report attempt.
3. Before a new dispatch executes, atomically persist its `started` fence.
4. After execution returns, atomically persist the result before moving the
   work to `awaitingAck`.
5. Report the original work identity. Remove the journal entry only after the
   existing durable Accepted/Stale acknowledgement contract succeeds.

On process startup, the Runner snapshots durable `started` entries before it
claims any new work. For a Workflow Agent dispatch with a non-empty persisted
task-run identity, it reports `status: unknown` through the existing result
route after connection. That is an explicit result-unconfirmed observation, not
a synthesized `WorkItemResult`: the Server matches the original
`runnerId`/`taskRunId`/`workId` tuple and enters its existing unknown/blocked
settlement. For an AgentJob dispatch, the same receipt retains the original
`runnerId`/`agentJobId`/`workId` tuple and moves the AgentJob to its durable
`Unknown` state; it does not enter the terminal failed path. Both owners retain
their work identity and refuse physical replay.

The Runner retains the `started` fence until the observation gets an Accepted
or Stale acknowledgement, then removes it atomically. A transport or
local-delete failure leaves the original fence durable and retries the same
observation. Only entries loaded before this process begins admitting work are
eligible. Current-process `started` entries, checks, ordinary tasks, and legacy
entries without the complete owner identity stay fenced; they are never
projected as unknown reports. This prevents an active execution from being
mistaken for a lost result and prevents the generic task fallback from turning
an unsupported work type into a failure.

This is identity redelivery, not physical execution replay. It recovers the
result-before-report crash window. A process that died while the physical
execution was still unresolved remains unknown and is not inferred from a
runtime binding, idle observation, or reconnect.

## Alternatives Rejected

- Re-running every dispatch returned after reconnect: the original Agent may
  have applied side effects, so this is blind replay.
- Treating Pi session activity or an idle runtime as a Workflow result: those
  are observations and do not contain the authoritative result and side-effect
  boundary required by the Workflow settlement contract.
- Removing `HasUnresolvedAgentResult` from dispatch rendering: this would turn
  unresolved work into duplicate physical execution rather than recovery.

## Server Receipt Admission

The Runner's completed journal entry is the only recovery artifact that can
reconstruct a Workflow result. It carries the original dispatch and full
`WorkItemResult`, including output, error, artifact-upload, and follow-up-task
fields. On restart, Runner places that entry in `awaitingAck` and reports it
through the existing Workflow result route. The Server accepts it only when
the persisted Workflow attempt still matches the original `taskRunId`,
`workId`, and authenticated `runnerId`; an unknown or blocked settlement is
still reportable under that same tuple.

The Server has no safe source for a terminal result from a `started` entry. The
Workflow work projection stores lookup and active-work facts, not a result.
AgentSession terminal observations and the runtime close event carry physical
activity, status, and exit information, but not the complete Workflow result
contract. Terminal task-log ownership is written after Workflow task
settlement and authorizes log upload only; it is not a result receipt. The
Workflow therefore must retain `unknown` or `blocked` when those are the only
facts available.

AgentJob has an explicit durable `Unknown` state for the same boundary. A
recovered AgentJob observation may enter that state only after the Runner has
presented the exact local `started` fence identity. The AgentJob report handler
must validate the current Runner and work identity before recording Unknown;
`status: unknown` must never call the normal success/failure terminalization
path. A subsequent authoritative terminal report is still allowed to resolve
the original Job, while an already-terminal Job returns the existing stale
acknowledgement.

This preserves a single recovery rule:

1. A completed receipt replays the original identity and may settle that
   original attempt exactly once.
2. A started-only record cannot be replayed physically or translated into a
   terminal result.
3. If the original result is permanently unavailable, the current operator
   escape hatch is explicit Workflow stop. This slice deliberately provides no
   replacement-execution command. Any future product capability that schedules
   replacement work after abandonment must allocate a new TaskRun and work
   identity, so a late report for the old attempt remains stale.

Workflow must not synchronously query AgentSession to infer a result. That
would turn an execution observation into an outcome and reintroduce a
cross-owner arbitration path. The Runner result receipt remains the one
authoritative cross-boundary payload.

## Recovery slice: unresolved-agent redelivery + started-fence reconciliation (D2/D4)

This slice implements the deadlock-breaker subset of the agreed runner-loss
design recorded in issue #570's run workspace (`runner-loss-work-recovery`,
decisions D2/D4 there): workflow-owned agent tasks whose result reporting was
lost across a runner restart recover without duplicate execution. The full
interruption-recording and deadline machinery (D1/D2 there) is intentionally
out of scope; the landed settlement arbitration (`Unknown` → `Blocked`) and
the work-result journal are reused unchanged.

Server (`DispatchService`): a run with an unresolved settlement is included
in desired redelivery for the recorded runner only, and only while the
settlement task is still `Running` with a full runtime binding. The rendered
dispatch reuses the translator path (settlement reconcile deletes snapshots)
and carries the recorded binding in a new optional `WorkDispatch.AgentRecovery`
block. Recovery renders do not reserve runner slots — they are probes, not
executions. Without a full binding the work stays absent (the deadline and
explicit-stop paths own it), because a binding-less redelivery to a runner
with no journal fence could not be reconciled and would re-execute.

Runner (`host.ts` admission + `mohist/pi` action): a dispatch carrying
`agentRecovery` never submits a new prompt. The pi action switches to
reconciliation: it inspects the bound session's recorded turn; a terminal
turn is adopted — its recorded outcome becomes the action result and the
normal executor tail (expect/artifacts/worktree/set-vars) runs unchanged; a
missing session or a foreign active turn reports the wire `unknown`, which
the server routes into settlement. A `started` fence hit by a recovery
dispatch re-arms its payload and executes the same reconciliation; fences
without a recovery dispatch still refuse silently. OpenCode recovery
dispatches are not executed in this slice (the OpenCode runtime does not yet
expose an API for adopting a terminal turn's facts); they report `unknown`
and adoption remains future work.

Capacity note: recovery renders do not reserve poll slots, but the runner
grain's claim gate still counts every Running-assigned run, so a runner at
capacity with lingering unresolved runs takes no fresh work until those runs
settle (recovery dispatches themselves are never capacity-gated) or an
operator stops them. Freeing that capacity automatically is the full design's
deadline machinery, out of scope here.
