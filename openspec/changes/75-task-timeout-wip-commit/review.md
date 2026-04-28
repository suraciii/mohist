# Review Report

## Verdict: PASS

## Dimensions

### Correctness: PASS

**T-001 — Timeout retry config** (`ralph-executor.ts:38`): `timeout: { maxAttempts: 3, retryable: true }` — correct. Test updated to match.

**T-002 / T-003 — WIP commit race condition** (`acp-session.ts:384-391, 778-785`): Both single-round and multi-round already `await onBeforeKill(cwd)` with try/catch setting `wipCommitted = false` on error. The issue description's diagnosis of fire-and-forget was correct for the codebase *before* issue #56, but the current code is already fixed. No code change needed — verified correct.

**T-004 — Task interface** (`context-assembler.ts:20`): `durations?: number[]` added correctly.

**T-005 — updateTaskInList** (`ralph-executor.ts:354-366`): The `Pick` type is extended to include `'durations'`. The spread logic `task.durations = [...(task.durations ?? []), ...updates.durations]` correctly appends new durations to existing ones. The caller always passes `[attemptDuration]` (a single-element array), so this accumulates one duration per attempt — correct.

**T-006 — Duration recording in executor loop** (`ralph-executor.ts:614-636, 641, 711, 719, 724`):
- `attemptStartTime` is recorded before `_acpSessionRunner` call (line 614).
- `attemptDuration` is calculated immediately after (line 636).
- Duration is recorded in all three exit paths: success (641), non-retryable failure (711), max-retries-exceeded failure (719), and retry-continue (724).
- Duration reflects actual wall-clock time (`Date.now() - attemptStartTime`), not the timeout threshold — correct per spec.

**T-007 — API durations** (`issues.ts:1504, 1512`): Type annotation extended to include `durations?: number[]`. Since tasks are read directly from `tasks.json` which now contains `durations` (written by `writeTasksFile`), the data flows through automatically — correct.

**T-08A — SSE live timing** (`useSSE.tsx`): New file replaces old `useSSE.ts`. All original SSE event handling logic is preserved. New `ralph_task_update` handler:
- On `status='started'`: clears previous timer, records `taskStartRef.current = Date.now()`, starts 500ms `setInterval`.
- On `status='completed'` or `'failed'`: clears timer, sets elapsed to `null`.
- Cleanup in `useEffect` return and separate cleanup `useEffect` both call `clearLiveTimer`.
- `LiveTaskContext` + `useLiveTask()` hook correctly expose `activeTaskId` and `activeTaskElapsedMs`.

**T-08B — TaskList component** (`TaskList.tsx`): New component with `DurationBadge`, `TaskRow`, and `TaskList`. Duration formatting handles ms, seconds, and minutes. Live timer shows spinner + elapsed time. Multi-attempt tasks show badge with `Nx` count and tooltip with per-attempt breakdown + total.

**Minor observation — `ralph_task_update` status mapping in SSE handler**: The handler only checks `'started'`, `'completed'`, and `'failed'` — it does not handle `'retrying'`. The `'retrying'` status currently leaves the live timer running, which is reasonable (the task is still active, just retrying). This is acceptable behavior.

### Complexity: PASS

- `updateTaskInList` remains concise (12 lines).
- `DurationBadge` component is well-factored at ~60 lines with clear branches.
- `useSSEInner` hook at ~130 lines is reasonable for SSE connection management + live timing.
- No copy-pasted code; shared `formatDuration` / `formatDurationShort` utilities.
- Cyclomatic complexity is within bounds for all functions.

### Test Coverage: PASS

- `ralph-executor.test.ts`: Updated assertions for timeout config (maxAttempts=3, retryable=true), 10-minute floor, and dependency validation message. All 73 tests pass.
- Pre-existing test failures (47) exist on master and are unrelated to this change (merge-queue, pipeline-checkpoint, priority, recover-issues tests).
- Backend build passes (`tsc`).
- Frontend typecheck passes (`tsc -b --noEmit`).
- No new test coverage for duration recording logic or frontend components — this is a gap but not a blocker since the logic is straightforward and the existing test infrastructure covers the config/executor paths.

### Security: PASS

- No SQL, command injection, or secret exposure risks.
- `durations` field is a simple numeric array read from/written to `tasks.json` (file already under the system's control).
- SSE live timer uses only `Date.now()` — no user-controlled input.
- API endpoints don't accept `durations` as input (read-only field).

### Spec Compliance: PASS

**T-001 — Timeout retry config:**
- [x] `FAILURE_CATEGORY_CONFIGS.timeout.maxAttempts === 3` — PASS (line 38)
- [x] `FAILURE_CATEGORY_CONFIGS.timeout.retryable === true` — PASS (line 38)
- [x] Typecheck passes — PASS

**T-002 / T-003 — WIP commit await:**
- [x] `onBeforeKill` callback is awaited before timeout result is returned — PASS (acp-session.ts:387, 781)
- [x] `wipCommitted` reflects actual WIP commit outcome — PASS (set from await result)
- [x] If `onBeforeKill` throws, `wipCommitted` is `false` and timeout proceeds — PASS (try/catch on lines 386-390, 780-784)
- [x] Typecheck passes — PASS

**T-004 — Task interface:**
- [x] Task interface includes `durations?: number[]` — PASS (context-assembler.ts:20)
- [x] Typecheck passes — PASS

**T-005 — updateTaskInList:**
- [x] Accepts `durations` in Pick type — PASS (ralph-executor.ts:357)
- [x] When provided, appended to `task.durations` — PASS (ralph-executor.ts:363-365)
- [x] Existing durations preserved — PASS (spread operator)
- [x] Typecheck passes — PASS

**T-006 — Duration recording:**
- [x] Duration recorded after each attempt (success or failure) — PASS (lines 641, 711, 719, 724)
- [x] Duration is actual elapsed time, not timeout threshold — PASS (`Date.now() - attemptStartTime`)
- [x] `task.durations` array accumulates across retries — PASS (each call appends to existing array)
- [x] Typecheck passes — PASS

**T-007 — API durations:**
- [x] `GET /:number/build-status` response type includes `durations` — PASS (issues.ts:1504, 1512)
- [x] Typecheck passes — PASS

**T-08A — SSE live timing:**
- [x] On `status='started'`, local startTime is recorded — PASS (useSSE.tsx:59)
- [x] `setInterval` updates elapsed time every 500ms — PASS (LIVE_TIMER_INTERVAL = 500)
- [x] On `status='completed'` or `'failed'`, timer stops — PASS (useSSE.tsx:65-68)
- [x] Typecheck passes — PASS

**T-08B — TaskList display:**
- [x] Completed task shows checkmark with duration (e.g., ✓ 15m) — PASS (DurationBadge with CheckIcon)
- [x] Failed task shows error indicator with duration (e.g., ✗ 28m) — PASS (DurationBadge with CrossIcon)
- [x] Multiple attempts show all durations in tooltip — PASS (tooltip with per-attempt breakdown + total)
- [x] Currently executing task shows live elapsed time — PASS (SpinnerIcon + formatDuration)
- [x] Typecheck passes — PASS

**T-009 — Tests and typecheck:**
- [x] All existing tests pass (47 pre-existing failures on master, 0 new failures) — PASS
- [x] Typecheck passes — PASS
- [x] Build succeeds — PASS

## Fix Suggestions

No error-level issues found. Minor suggestions:

1. **`useSSE.tsx:65-68`** — The `ralph_task_update` handler does not reset `activeTaskId` on `'retrying'` status. This means if a task retries, the timer correctly keeps running (since `'retrying'` doesn't match `'completed'` or `'failed'`). This is acceptable behavior but worth documenting.

2. **`TaskList.tsx:167`** — `isLive` is determined by `isCurrent && !task.passes && task.error === null`. If a task has already failed (error is set) but is currently being retried, `isLive` will be `false` because `task.error` is non-null from the previous attempt. The live timer will still show (via `activeTaskId`), but the `isLive` check could prevent it. Verify that the retry path updates `task.error` back to `null` before the retry starts. Looking at `ralph-executor.ts:724`, the retry path only updates `durations`, not `error`. However, since the SSE `activeTaskId` mechanism bypasses `isLive` (the `activeTaskElapsedMs` is passed directly when `task.id === resolvedCurrentId`), this is not a bug — but the logic could be simplified.

3. **No test coverage for duration recording** — Consider adding a test that verifies `task.durations` is populated after a successful/failed attempt. The existing test infrastructure mocks `_acpSessionRunner`, so this would be straightforward to add.
