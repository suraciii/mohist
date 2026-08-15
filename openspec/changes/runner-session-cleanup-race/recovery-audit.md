# Cleanup Race Audit

## Current evidence

- `packages/runner/src/runtime/worktree-cleanup.ts:37-51` intentionally
  preserves the original `session` when constructing the cleanup prompt.
- `packages/runner/src/runtime/worktree-enforcement.ts:326-344` reinvokes the
  resolved Action for each cleanup attempt, with the same `DispatchWorkItem`.
- `packages/runner/src/runtime/executor.ts:245-255` builds the cleanup host
  from that same work item and calls the original Action definition again.
- `packages/runner/src/actions/pi.ts:127-155` reports every normal turn through
  the Workflow `session.input` receipt path and returns
  `session-reporting-failed` when that input is rejected.
- `packages/runner/src/server/runtime-event-outbox.ts:632-712` partitions
  Workflow records by complete execution identity. The current scheduling
  groups are therefore also different when a reused Session starts a new turn.
- `packages/server/src/Mohist.Server/Api/RunnerRoutes.cs:437-490` requires
  Workflow `session.input` to be delivered alone and routes it to the normal
  Workflow input admission. There is no dedicated cleanup admission on the
  current master.
- `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:1914-2007`
  creates a Workflow-bound turn and invokes the Workflow execution binding
  port. It is not a cleanup-turn contract.

The live failures for #555, #557, and #560 all report the same mechanism:
cleanup attempted a normal `session.input`, the Server rejected it, and the
proposal remained untracked. The Runner-only scheduling fix is necessary to
prevent terminal facts and the next boundary from racing, but it is not
sufficient because the current Server has no valid cleanup admission.

## Decision

Do not land a runner-only retry, delay, new Session name, or silent dirty-file
discard. The next implementation slice must add the Server-owned cleanup
admission and its identity-fenced Session runtime-event route, then add the
Runner outbox scheduling fence and Pi/generic regression together.
