# Runner Work Confirmation

An owner record can outlive an execution observation. Consumers need a source
time tied to the work, not a fresh timestamp produced by reading its owner.

## Confirmation source

An active-work row is an owner-ledger fact: it says who owns the work, not that
a process is executing it right now. Each row therefore carries
`confirmedAt` — the poll receipt time of the last poll that named
`{ownerKind}:{ownerId}:{workId}` as in-flight or awaiting ack. The Runner grain
stamps it when it accepts the poll; reading status never renews it, and a
confirmation is retained for ten minutes after it was last named so a stale time
stays reportable. `confirmedAt` is `null` when the Runner has not named the work
recently, and it never confirms work owned by a different process generation.
Confirmations are observation facts, not durable state: a Server restart or a
lost observation drops them until the next poll names the work again, and
unknown is never confirmation. Consumers that need "is this work still
executing" must read `confirmedAt` against its freshness window instead of
treating a present row as fresh.

## Consumers

[Session execution evidence](../../session/execution-evidence/spec.md) owns the
freshness window and current-execution interpretation. Retaining or losing a
confirmation does not by itself release ownership or capacity. The
[Runner status projection](../presence-and-capacity/spec.md#runner-status-projection)
keeps ownership, capacity, and observations separate.

## Acceptance scenarios

- A poll naming one work key renews that key only, not every retained owner.
- A read or a heartbeat that does not name the work leaves its confirmation
  time unchanged; it does not release the retained owner.
- After a Server restart, confirmation remains unknown until a new poll names
  the work. A confirmation from another process generation cannot confirm it.
## Execution Ownership

Runner owns host-specific effects because they are replaceable execution state.
Server owns durable work decisions because a Runner can disappear. Unreported or
uncommitted files are not durable results.

For one task, Runner prepares an isolated Workspace, resolves the declared Action
input, invokes the execution backend, and reports facts and outputs. It validates
output expectations before reporting success and reclaims the Workspace when the
Workflow no longer needs it. The complete Action contract is in
[Action Contracts](../../workflow/actions/spec.md).

Runner owns the complete process tree for every host command. A command result
includes output produced before exit. A leftover subprocess cannot keep the
result open or write into later work.

When a Workflow Workspace is first materialized, Runner transfers only the
repository data needed to establish its base and run branches. Later Stages can
rebase and integrate that branch. Transfers remain bounded, and failed
materialization does not publish or retain a partial Workspace.
