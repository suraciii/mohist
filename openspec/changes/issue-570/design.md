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
settlement. The Runner retains the `started` fence until that observation gets
an Accepted or Stale acknowledgement, then removes it atomically. A transport
or local-delete failure leaves the original fence durable and retries the same
observation.

Only entries loaded before this process begins admitting work are eligible.
Current-process `started` entries, checks, AgentJobs, ordinary tasks, and
legacy entries without a complete Workflow Agent attempt identity stay fenced;
they are never projected as unknown reports. This prevents an active execution
from being mistaken for a lost result and prevents the generic task fallback
from turning an unsupported work type into a failure.

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

The Server has no safe source for a result from a `started` entry. The
Workflow work projection stores lookup and active-work facts, not a result.
AgentSession terminal observations and the runtime close event carry physical
activity, status, and exit information, but not the complete Workflow result
contract. Terminal task-log ownership is written after Workflow task
settlement and authorizes log upload only; it is not a result receipt. The
Workflow therefore must retain `unknown` or `blocked` when those are the only
facts available.

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
