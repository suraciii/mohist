## Why

An Epic whose linked issues are split between `done` and `cancelled` is stuck: every issue has already reached a terminal state with no further execution to do, yet the Epic cannot complete because the current readiness rule demands "all linked issues delivered". The completion condition should express "is there any open work left on this product goal", not "did every linked issue get delivered". This blocks auto-done, manual Mark Done, and the detail-page ready-to-done indicator for epics that are effectively finished.

## What Changes

- Epic readiness is redefined around **terminal** linked issues: an Epic is ready to complete when **all** linked issues are terminal (`done`/`completed` **or** `cancelled`), i.e. when no linked issue is open (`backlog`, `draft`, `in_progress`, `blocked`, `paused`).
- Auto-done, manual Mark Done, and the detail/list `readyToMarkDone` indicator now share this single terminal-based readiness rule.
- `deliveredCount` is unchanged: it still counts only `done`/`completed` issues. A `cancelled` issue is **not** counted as delivered; it simply no longer blocks completion.
- A `cancelled` linked issue is treated as out-of-scope for completion blocking (and continues to be excluded from next-issue selection).
- The legacy "Cancelled linked issue prevents auto-done" behavior is **removed**; existing tests asserting that cancelled issues block Epic completion are updated to the terminal/open semantics.

## Capabilities

### New Capabilities
<!-- None. This is a semantics change to existing Epic completion behavior; no new capability is introduced. -->

### Modified Capabilities
- `epic-lifecycle`: The readiness/condition for the `running → done` transition changes from "all linked issues delivered" to "no open linked issue" (all linked issues terminal). Affected requirements: auto-done on issue completion (incl. the "Cancelled linked issue prevents auto-done" scenario), the lifecycle state-machine transition condition, resume re-evaluation, and autonomous-advancement reconciliation guards.
- `epic-list-query`: The `readyToMarkDone` computation changes to the terminal-based rule so a cancelled-only-remaining epic is reported ready; `deliveredCount` semantics (done/completed only) are unchanged.

## Impact

- **Server / Epic domain** (`packages/server/.../Epic/`): the shared readiness computation backing `EpicProgress.IsCompleted` / `readyToMarkDone` / `MarkDone` precondition changes from "undelivered count == 0" to "open (non-terminal) linked issue count == 0". Auto-done, manual Mark Done, resume, and the detail/list read-models all consume this single computation.
- **Read models**: epic list and detail `readyToMarkDone` now returns `true` when all linked issues are terminal; `deliveredCount` and `totalIssueCount` are unaffected.
- **Tests**: existing Fake-based tests asserting "cancelled linked issue blocks Epic completion / prevents auto-done / fails readyToMarkDone" must be rewritten in terminal/open domain language. New coverage for the mixed `done` + `cancelled` epic (e.g. Epic #18) becoming completable.
- **No schema/persistence changes**: no new Epic state, no new fields; `cancelled` issue status semantics are unchanged.
- **Non-goals** (per issue): no new Epic states; no change to issue `done`/`cancelled` semantics; no change to `deliveredCount` meaning; Epic close non-destructive semantics tracked separately by #179.
