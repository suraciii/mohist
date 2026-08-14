# Runner Runtime Readiness Witness

## Problem

Runner runtime readiness is currently a process-local fact. `OpenCodeRuntime`
and `PiRuntime` expose `ready()` only to the Runner, while the Server poll
request carries only `inFlight` and `awaitingAck`. The Server therefore cannot
prove that the runtime required by a pending work item is ready before it
claims the item.

The current workflow path makes this ordering visible: `DispatchService`
claims a workflow in `ClaimAndRenderWorkflowAsync`, then loads the run and
translates the claimed item. The translation is the first point at which
`mohist/pi` or `mohist/opencode` is known for the workflow item. A failed or
deferred translation leaves a claimed item without a dispatch.

## Contract

The Runner sends a runtime readiness witness in every poll. A witness is an
ephemeral observation bound to the Runner connection and contains:

- `runtime`: the canonical runtime id, for example `pi` or `opencode`;
- `ready`: whether this runtime can accept new work now;
- `generation`: a monotonically increasing runtime instance/fence owned by the
  Runner; and
- `connectionGeneration`: the registration identity that produced the
  observation.

The Server treats a missing, malformed, stale, or `ready=false` witness as
unknown for new claims. It never treats a runtime catalog as a readiness
witness. The witness is not durable work state and cannot settle, replay, or
replace a work result.

## Admission sequence

For each pending candidate, `DispatchService` must resolve the candidate's
runtime without mutating its owner, then apply the witness predicate, and only
then call the owner claim operation:

```text literal
pending candidate
  -> resolve immutable runtime identity
  -> match current runner connection + runtime generation + ready=true
  -> owner ClaimNext / ClaimAgentJob
  -> render and persist dispatch snapshot
```

Workflow runtime resolution must be a read-only projection of the pending
`WorkItem`; it must not call `ClaimNextAsync` merely to discover `uses`. Agent
Job runtime resolution must use its persisted dispatch snapshot. If either
projection is unavailable, the candidate remains pending for a later poll.

Redelivery is separate from admission. Work already reported by the Runner as
`inFlight` or `awaitingAck` remains owned by that Runner and may be delayed
while its runtime recovers. The Runner must continue polling to report and
acknowledge that held work; it must not acquire new work and then hide it in an
unbounded deferred queue.

## Fencing and races

A readiness witness is a claim-time admission fence, not a guarantee that an
external runtime cannot fail immediately after the poll. If readiness changes
after a successful claim, the claimed work follows the existing in-flight and
result-uncertain protocol. A later connection or runtime generation cannot
reuse an older witness.

The Server must not infer readiness from a successful HTTP poll, presence,
heartbeat, model catalog, runtime session file, or reconnect. These facts have
different owners and lifecycles.

## Current implementation boundary

This change is design-only on `origin/master=aed884418`. The existing wire
types have no witness field, the Server has no generation-aware admission
predicate, and workflow candidate queries expose only a run id. Adding a
field without changing candidate resolution would protect AgentJob claims but
leave workflow claims unsafe. Production implementation is therefore deferred
until the protocol and both owner projections can be landed atomically.

## Verification contract

The implementation must add focused Server and Runner tests for the scenarios
in the OpenSpec change before enabling the new field in production. Existing
poll/reconciliation tests remain responsible for redelivery and capacity.
