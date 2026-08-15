# Issue 596 Follow-up: Managed Runtime Transaction Payload GC

## Problem

Managed runtime updates create a source snapshot, a writable build tree, and a
candidate payload under `runtime/transactions/<id>`. A successful update moves
the candidate into `runtime/releases`, but older transaction payloads can stay
on disk indefinitely. The per-commit cleanup from the first Issue 596 slice
only handles the transaction that just committed; it does not reclaim history
left by older CLI versions or interrupted updates.

## Change

Add a local exclusive update lock and a conservative history collector to the
CLI managed-runtime transaction owner. The collector runs while the lock is
held and removes only the disposable payload directories of transactions whose
durable state is an old successful `verified` or `rolled-back` state.

The collector is fail-open. An unavailable lock, malformed pointer, unreadable
state, symlink, unknown status, or deletion failure leaves the runtime intact
and is reported as a diagnostic; it does not make a successful update fail.

## Safety Boundary

The collector never deletes `active.json`, `verified.json`, any transaction
`state.json`, `cli-launcher.previous`, recovery records, candidate transaction
payloads, or anything below `runtime/releases`. It does not infer liveness from
directory age, generation, or disk size.

This is managed-update storage hygiene for the #567 boundary. It does not
recover a Runner `started` work item, synthesize a result, or change the #570
result-delivery contract.
