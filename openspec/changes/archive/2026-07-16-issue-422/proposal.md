## Why

After a task exhausts its automatic recovery budget, `mo issue retry` carries that exhausted state into the new attempt, so the documented manual recovery action cannot restart the review-fix loop. A manual retry must begin a fresh recovery round with the full declared budget while every uninterrupted automatic round remains bounded by that declaration.

## What Changes

- Keep a task's declared recovery budget and handlers immutable across workflow loading, task history, dispatch, and automatic self-retry.
- Track the remaining automatic recovery allowance as state of the current recovery round; each automatic recovery consumes one allowance and a continuous round never exceeds the declared budget.
- Make manual retry create a fresh task attempt with the full declared recovery budget, even when the preceding automatic recovery round exhausted its allowance.
- Keep recovery declarations carried by persisted task history and event/dispatch data identical to the workflow declaration instead of reflecting budget consumption.
- Preserve existing `when` matching, first-handler selection, recovery-task ordering, self-retry construction, and ordinary failure behavior after budget exhaustion. Approval, rerun, and rerun-from-stage semantics are unchanged.

## Capabilities

- `workflow-task-recovery`: The recovery-budget lifecycle for workflow tasks, including immutable recovery declarations, per-round remaining allowance, bounded automatic self-retries, and a fresh full-budget recovery round after manual retry.

## Impact

- **Server workflow domain** (`packages/server/src/Mohist.Server/Workflow/`): task definition/run state, runtime task insertion, failed-task retry reconstruction, workflow persistence, and Orleans serialization.
- **Server-runner contract**: workflow work items, rendered dispatches, and runner-produced follow-up task reports must carry recovery declaration and per-round execution state separately.
- **Runner** (`packages/runner/src/runtime/`, `packages/runner/src/core/`, `packages/runner/src/server/`): recovery evaluation and self-retry construction consume and forward the per-round allowance without rewriting the declaration.
- **Tests**: server workflow specs and runner recovery specs cover budget exhaustion, manual retry with a fresh budget, the unchanged per-round limit, and declaration immutability through the task lifecycle.
- No public CLI, Web, or workflow YAML command/schema changes; no new external dependencies.
