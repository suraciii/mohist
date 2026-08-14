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
