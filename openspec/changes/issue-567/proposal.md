# Issue 567: Managed Runner Update Interrupt Boundary

## Problem

Managed `runner` and `full` updates can build a complete release and activate
its pointer before the currently connected Runner is told to stop admitting
new work. A restart can therefore race with new dispatch, while an
unconfirmed interrupt must not activate or restart a candidate.

## Change

Require a connected Runner identity and an authoritative `interrupted` result
before managed Runner activation. Keep the current active target and service
unchanged when that precondition is not confirmed. If staging has already
moved a candidate into a release and fails before active-pointer activation,
remove only that transaction's exact staged release so a retry can rebuild it.

This is the admission-fence phase. Each confirmed fence has an opaque,
caller-owned interruption id. If activation, restart, verification, or the
update command itself fails after confirmation, the CLI releases only that
same fence through the Runner API. The server persists the fence so a server
restart cannot accidentally reopen admission, and a later update cannot be
unfenced by an older CLI invocation.

The subsequent runtime-recovery phase is specified in [`recovery.md`](recovery.md):
it requires a durable, execution-bound receipt before an interrupted Agent
attempt can be recovered. That phase is deliberately not satisfied by a
Runner drain, cancellation rollback, or reconnect.

## Safety Boundary

This change closes admission before a managed Runner restart; it does not
re-execute interrupted work, infer terminal results, or recover an in-flight
execution from process state. Server-side reconnect remains the boundary that
clears the Runner drain state and lets the existing durable work protocol
reconcile preserved work.
