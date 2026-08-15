# Issue 570: Explicit Replacement for Unconfirmed Workflow Agent Work

## Problem

A Runner journal entry that reached only `started` has an exact dispatch
identity but no authoritative result. The existing Workflow settlement keeps
that attempt `unknown` and then `blocked`, which correctly prevents a second
physical execution. Its only operator escape hatch is terminal Workflow stop.

Some operational incidents need a deliberately new attempt after the original
physical target has been recorded as `target-missing` or the result has been
recorded as `unknown`. Reusing the old task or work identity would allow a late
receipt to settle the replacement, and treating Session idle, Turn state, or a
task log as a result would guess an outcome.

## Proposed Change

Add one explicit, idempotent Workflow-run control that supersedes a blocked
unconfirmed Agent attempt and creates a new pending TaskRun. The control is
available only after the original tuple has a persisted `target-missing` or
`unknown` observation and has reached blocked attention. It requires the
operator to provide the exact original tuple, a request id, a reason, and an
explicit confirmation.

The old attempt becomes a terminal, immutable superseded record. The new
attempt receives a distinct TaskRun id and receives a distinct work id only
through the ordinary later claim path. A late receipt for the old tuple is
stale and has no artifact, output, follow-up, or status effect.

## Non-goals

- Do not infer a result from Session idle/completed, AgentTurn state, runtime
  presence, logs, a reconnect, or a missing process.
- Do not replay the old Prompt or reuse its TaskRun or work identity.
- Do not use retry, rerun, stop, approval, or a Runner startup callback as the
  replacement operation.
- Do not alter existing live blocked Runs as part of this design-only change.
