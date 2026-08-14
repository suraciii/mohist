# Design: Runtime-Owned Agent Recovery Receipt

## Current Boundary

`WorkResultJournal` already has the only safe report-replay protocol for a
known result: persist the exact dispatch identity as `started`, persist the
`WorkItemResult` as `completed`, then delete it only after the Server returns
a durable acknowledgement. `RunnerHost.executeAndTransition` persists a
normal returned result before its first report, and startup reloads completed
entries into `awaitingAck`.

That protocol cannot resolve a `started` entry. A process crash can occur
after physical effects begin and before an Action returns a result. The Pi
runtime's recovery surface resolves only the bound session and its
`activeTurn` state; its message history is not an execution-scoped terminal
receipt. `DispatchService` deliberately suppresses redelivery when
`WorkflowRun.HasUnresolvedAgentResult()` is true. These boundaries are
necessary to prevent duplicate Agent execution.

The existing managed update API records only a Runner drain and a list of
active work ids. It creates neither a receipt nor a new Workflow execution
attempt. The original `AgentResultSettlement` freezes one AgentSession,
AgentTurn, runtime, and runtime-session binding, and rejects a different
binding for the same work. Therefore a restarted runtime cannot safely reuse
that binding for a new physical turn.

## Receipt Contract

The next implementation needs one durable `RuntimeRecoveryReceipt` with this
immutable identity:

```text
workflowRunId + taskRunId + workId + runnerId
agentSessionId + agentTurnId
runtime + runtimeSessionId
recoveryGeneration + receiptId
```

The receipt has exactly one of these payloads:

1. `terminal-result`: the complete, already-normalized `WorkItemResult` and
   a fingerprint of that payload.
2. `update-interrupted`: an update-operation id and a runtime-confirmed
   statement that the exact bound turn is no longer executing. It contains no
   task outcome and cannot be manufactured from an idle observation.

The runtime adapter writes its receipt to a Runner-local atomic store before
the Runner sends it. The Runner retries that same receipt until the Server
acknowledges it. A completed journal result can be represented by the first
payload, but an interruption needs its own state; mapping it to a failed or
unknown `WorkItemResult` would respectively fail the workflow or re-enter the
existing blocked path rather than recover it.

## Server Arbitration

The WorkflowRun owns receipt arbitration in the same persistence transaction
as task state. It accepts an exact duplicate receipt as a no-op and rejects a
mismatch or any receipt for a terminal, stopped, or different binding.

For `terminal-result`, the existing authoritative result settlement remains
the only transition: the payload is applied exactly once and normal report
acknowledgement retires the local receipt.

For `update-interrupted`, Workflow must first verify a matching durable update
operation fence. It then records the original attempt as interrupted history,
allocates `recoveryGeneration + 1` and a new AgentTurn binding, and makes one
new dispatch eligible. Only after that commit may the Runner retire the local
interruption receipt. The new AgentTurn must receive a separate input
delivery identity, while the original turn remains immutable for transcripts,
late events, and stale-report rejection.

This is intentionally not a mutation of the original settlement binding.
Changing that binding would let late events from the interrupted physical turn
settle the replacement execution.

## Failure Rules

| Observed fact | Server action |
| --- | --- |
| Exact terminal receipt, delivery fails | Retain and replay the same receipt |
| Exact confirmed update interruption, delivery fails | Retain and replay the same receipt |
| Runner update while receipt is not yet durable | Do not restart as recovered; retain the existing unresolved fence |
| OOM or process loss with only `started` | Preserve `agent-result-unconfirmed`; no re-execution |
| Idle/missing runtime or transcript text | Preserve `agent-result-unconfirmed`; no inference |
| Receipt identity or payload mismatch | Reject and retain the original settlement |

The update coordinator must not turn a direct system-service restart into a
recovery claim. It needs an acknowledgement boundary from the old Runner that
all affected work has either written a terminal receipt or a confirmed
interruption receipt. A timeout or old-process loss leaves the update outcome
explicitly unresolved instead of pretending recovery succeeded.

## Delivery Slices

This cannot be a Runner-only change. The smallest complete product slice is:

1. Add the durable Server update-operation fence and receipt port, including
   exact binding and replay validation.
2. Add the runtime-neutral Runner receipt journal and acknowledgement loop;
   Pi and any other enabled Agent runtime must emit only the generic receipt
   contract.
3. Extend AgentSession/Workflow attempt creation so a confirmed interruption
   creates a new turn and delivery identity without changing the prior one.
4. Make managed update wait for receipt acknowledgement or report the named
   work as unresolved; then add the CLI/read-model presentation.

Focused tests must cover terminal replay, interruption replay, mismatched
binding rejection, stale old-turn events after replacement, duplicate receipt
idempotence, update timeout without receipt, and OOM with only `started`.

### Implementable Receipt Slice

The first implementation slice is narrower than update recovery: when an
Action has already returned a normalized `WorkItemResult`, a run-lifetime
abort does not discard that result. `RunnerHost` persists it through the
existing `WorkResultJournal` before attempting the existing durable report.
If the first report fails, normal startup reload replays that completed entry
with the original work identity and never executes the Action again.

This path is runtime-neutral and does not classify cancellation as success.
An Action that throws after the abort, a process OOM, and every historical
`started` entry still have no receipt and retain the unresolved fence. It does
not create an update operation, a replacement AgentTurn, or a replacement
dispatch; those remain the four-part recovery protocol above.

## Rejected Alternatives

- **Replay every `started` dispatch:** a prior runtime may have performed
  effects before the process died.
- **Use Pi session messages or `activeTurn` as a result:** neither value is
  tied to the frozen Workflow execution or its task completion boundary.
- **Reuse the original AgentTurn for recovery:** delayed terminal events could
  complete the replacement attempt.
- **Treat graceful host cancellation as a failed WorkItemResult:** it avoids
  a journal fence but changes a requested update into a Workflow failure and
  does not satisfy the recovery contract.
