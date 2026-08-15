# Design: runner work-result persistence recovery

## Decision

`WorkResultJournal` owns a small two-stage durability state for an exact
result that has already returned from execution:

1. `ready`: every completed journal entry is durable and may be reported.
2. `persistence-pending`: one or more completed entries are retained only in
   process memory because the last journal write failed.  Admission and result
   reporting are both blocked.

Corrupt, unreadable, or identity-conflicting journal state remains
unavailable and fail-closed.  It is not treated as a transient write failure.

On a recoverable completion write failure, the journal retains the exact
`DispatchWorkItem` and `WorkItemResult` in its entry instead of reverting it
to `started`.  The host leaves that work in its process in-flight set, so a
redelivery cannot execute it twice.  The host does not call the owner report
endpoint for that entry until a later successful journal persistence makes it
durable.

The existing successful control-plane poll is the recovery boundary.  After
one arrives, the host asks the journal to flush retained entries.  A successful
flush promotes all completed entries through the existing report-and-ack
path.  A failed flush leaves them retained and unavailable for admission;
there is no independent timer or runtime-specific recovery loop.

## Crash boundary

If the process exits before the pending completion is durably flushed, no
result has been reported.  The on-disk `started` entry remains a hard recovery
fence.  A new process must not infer a result or replay physical execution;
it remains unresolved until an authoritative result receipt or an explicit
owner action settles it.

This makes the repair runtime-agnostic: Pi, OpenCode, and generic actions all
use the same `DispatchWorkItem` / `WorkItemResult` journal boundary.  No agent
session or physical-turn observation participates in result arbitration.

## Terminal task logs

This slice starts only after `executeAndTransition` has an exact returned
`WorkItemResult`.  A task-log snapshot failure before that return still cannot
be converted into a successful result by observing an idle session or a turn
event.  Its durable recovery needs a separately specified terminal receipt or
snapshot contract.  When the host synthesizes a returned failure for that
exception, this slice preserves that exact returned failure rather than losing
it in a second journal write failure.
