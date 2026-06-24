## Why

When every issue under an epic finishes, the user must still navigate to the epic and click "Mark Done" — a redundant trailing step. The system already computes the "ready" condition (`EpicProgress.IsCompleted` across all linked issues); it should apply it automatically on the write-path when an issue completes, so the epic transitions to `done` with no manual chore.

## What Changes

- Epic automatically transitions `active → done` when all linked issues are complete (`done`/`completed`), triggered by issue completion rather than a manual action.
- Auto-done reuses the existing readiness check (`MarkDone` + undelivered-number computation); no new "ready" definition.
- Paused epics are **excluded** from auto-done (paused = do not advance, including auto-completion). After `resume`, if the condition already holds, the epic auto-dones.
- Manual "Mark Done" remains available for edge cases (early close, missed triggers).
- No behavior change for epics containing `cancelled` issues — they still won't auto-done, matching today's manual behavior.

## Capabilities

### New Capabilities
- `epic-lifecycle`: Governs epic status transitions and their invariants, including the new event-driven auto-`done` transition and its interaction with the `paused` state.

### Modified Capabilities
<!-- None. The existing `SetStatusAsync("done")` HTTP flow gains an internal auto-invocation path, but response semantics and the endpoint contract are unchanged, so no http-api spec delta is required. -->

## Impact

- **Server / Epic domain** (`packages/server/src/Mohist.Server/Epic/`):
  - `EpicGrain` gains an entry point (e.g. `OnIssueCompletedAsync`) invoked when a linked issue reaches `done`/`completed`, which runs the existing `MarkDone` undelivered-number check and transitions to `done` when satisfied.
  - `Epic.Transitions.cs` / `EpicGrain.SetStatusAsync` readiness logic reused as-is.
- **Server / Issue write-path**: the place that flips an issue to `done`/`completed` must signal the owning epic grain (event-driven or direct grain call) so the check fires reliably.
- **Orleans messaging**: a new grain-to-grain call (issue → epic) or a stream subscription; must be idempotent and tolerate races (issue completed, epic re-checked, already done).
- **Paused interaction (#173)**: auto-done must short-circuit when epic status is `paused`; the `resume` path must re-evaluate and auto-done if conditions now hold.
- No DB schema changes (status already persisted); no Web/UI changes required — the board card reflects `done` naturally.
- Tests: add Fake-based tests for (a) all-complete → auto-done, (b) paused excluded, (c) resume-then-auto-done, (d) cancelled issue → no auto-done regression, (e) manual Mark Done still works.
