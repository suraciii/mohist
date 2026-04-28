## Why

Task timeout is a leading cause of Ralph loop failures, and the current configuration treats it as non-retryable (`maxAttempts: 1, retryable: false`). For transient timeout events caused by LLM provider latency spikes or network jitter, this forces an immediate build stage failure requiring manual intervention. Additionally, the WIP commit mechanism — which saves in-progress work before killing a timed-out agent — has a race condition where the commit completion is not awaited before the timeout is signaled, causing the system to misclassify `timeout_with_wip` as plain `timeout` and lose the resume context. Finally, the system provides no visibility into how long each task attempt takes, making it impossible to distinguish a legitimately slow task from an environmental issue.

## What Changes

- **`packages/cli/src/openspec/ralph-executor.ts`**: Change `timeout` failure category config from `{ maxAttempts: 1, retryable: false }` to `{ maxAttempts: 3, retryable: true }`. Record `Date.now()` timestamps at task attempt start/end and append actual elapsed milliseconds to `task.durations` after each attempt (success or failure).

- **`packages/cli/src/agent-runtime/acp-session.ts`**: Fix the `onBeforeKill` fire-and-forget race in both `runAcpSession` (single-round) and `createAcpConnection`/`AcpConnection.prompt` (multi-round) by awaiting the `onBeforeKill` promise before resolving the `'timeout'` signal, ensuring `wipCommitted` reflects the actual commit result.

- **`packages/cli/src/openspec/context-assembler.ts`**: Add `durations?: number[]` to the `Task` interface.

- **`packages/cli/src/openspec/ralph-executor.ts`** (updateTaskInList): Extend to accept `durations` in the `Partial<Pick<Task, ...>>` update payload.

- **`packages/cli/src/api/issues.ts`**: `/tasks` and `/build-status` endpoints return `durations` field from the tasks.json file (already returned via direct file read, no structural change needed beyond ensuring the field is typed correctly in the response).

- **`packages/cli/web/src/lib/types.ts`**: Add `durations?: number[]` to the `Task` type.

- **`packages/cli/web/src/components/TaskList.tsx`**: Display elapsed time per task: completed shows last duration, failed shows last duration with error indicator, multi-attempt shows all durations in a tooltip or inline format. Live task shows running elapsed time updated via `setInterval`.

- **`packages/cli/web/src/hooks/useTaskProgress.ts`**: On `ralph_task_update` with status `started`, record local `startTime`. Use `setInterval` to compute and display live elapsed time for the active task.

## Capabilities

### New Capabilities

- `task-duration-tracking` — Each task attempt records its wall-clock duration in milliseconds. The final duration is the actual elapsed time (not the timeout threshold), which is persisted in `tasks.json` and surfaced via the API.

- `timeout-task-retry` — Task timeout failures are now retryable with up to 2 automatic retries (3 total attempts). This applies only to the `timeout` category; `timeout_with_wip` retains its existing 2-retry config.

- `wip-commit-await` — The `onBeforeKill` hook is awaited before the timeout resolution is confirmed, ensuring the `wipCommitted` result accurately reflects whether the WIP commit succeeded.

### Modified Capabilities

- `ralph-task-execution` — The timeout handling section of the Ralph executor spec currently lists `Timeout | >30min execution | No | -`. This row changes to `Timeout | >30min execution | Yes | 3 total`. The failure categorization table and retry behavior scenarios require updates to reflect retryable timeouts and WIP commit race condition fixes.

## Impact

- **Code**: `ralph-executor.ts` (failure config + duration recording), `acp-session.ts` (await onBeforeKill), `context-assembler.ts` (Task interface), `updateTaskInList` helper (durations field), `api/issues.ts` (response typing), web types and components.
- **API**: `/tasks` and `/build-status` responses gain a new `durations?: number[]` field in each task object. This is a backward-compatible additive change.
- **Persistence**: `tasks.json` schema gains an optional `durations` array per task. Existing files without `durations` are unaffected.
- **Frontend**: TaskList and useTaskProgress handle a new `durations` field. No breaking changes to existing event types.
