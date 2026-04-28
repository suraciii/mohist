## Context

Ralph executor (`ralph-executor.ts`) handles task execution via ACP sessions with a per-task timeout. Currently, the `timeout` failure category is configured as non-retryable (`maxAttempts: 1, retryable: false`). When a task times out, the `onBeforeKill` hook (which creates a WIP commit) is called but not awaited — this is a fire-and-forget operation. This creates a race condition: the timeout signal resolves before the WIP commit completes, causing the system to classify the failure as plain `timeout` instead of `timeout_with_wip`, losing the ability to resume from saved progress.

Additionally, the system does not record or expose task execution durations. Users cannot see how long each attempt took, making it difficult to distinguish a legitimately slow task from an environmental issue.

## Goals / Non-Goals

**Goals:**
- Enable automatic retry for timeout failures (up to 2 retries, 3 total attempts)
- Fix the WIP commit race condition so `wipCommitted` accurately reflects whether the commit succeeded
- Record wall-clock duration (ms) of each task attempt and persist to `tasks.json`
- Expose `durations` via `/tasks` and `/build-status` API endpoints
- Display task durations in the frontend TaskList component with live updates for in-progress tasks

**Non-Goals:**
- Implement a dynamic per-task timeout that recalculates on retry (perTaskTimeout is computed once at loop entry)
- Implement a Ralph loop overall stage timeout
- Modify the `timeout_with_wip` category configuration (it retains its existing 2-retry config)

## Decisions

### D1: Change timeout failure category config from `{ maxAttempts: 1, retryable: false }` to `{ maxAttempts: 3, retryable: true }`

**What:** `FAILURE_CATEGORY_CONFIGS.timeout` in `ralph-executor.ts:38`

**Why:** The existing configuration causes immediate failure on any timeout. Transient timeouts from LLM provider latency spikes or network jitter should be retried automatically. The `timeout_with_wip` category already has `{ maxAttempts: 2, retryable: true }`; the plain `timeout` category should match this intent with 3 total attempts.

**Alternatives considered:**
- Setting `maxAttempts: 2` (2 total): Insufficient for transient issues where 2 retries might be needed
- Leaving `timeout` as non-retryable and only retrying `timeout_with_wip`: The WIP commit race condition fix (D2) will handle categorization correctly, so plain `timeout` (without WIP) should still be retryable

### D2: Await `onBeforeKill` before resolving the timeout signal in both single-round and multi-round sessions

**What:** In `acp-session.ts`, the `onBeforeKill` callback is currently fire-and-forget at lines 384–391 (single-round `runAcpSession`) and lines 778–785 (multi-round `AcpConnection.prompt`). The timeout Promise resolves immediately when the timer fires, before the WIP commit operation completes.

**How:** Wrap the `onBeforeKill` call in a try/catch that awaits the result before setting `wipCommitted` and returning. This ensures the classification decision (timeout vs timeout_with_wip) is made only after the WIP commit outcome is known.

**Why:** The WIP commit operation is async and can take several seconds. Resolving the timeout signal before it completes means `wipCommitted` in the result always reflects the pre-timeout state (false), not the actual outcome. Fixing this ensures `categorizeFailure` in `ralph-executor.ts:659` correctly distinguishes `timeout_with_wip` from `timeout`, enabling the resume context to be injected into retry prompts.

**Alternatives considered:**
- Using a staged timeout (start `onBeforeKill` earlier): Adds complexity; the await approach is straightforward
- Adding a `wipCommitted` callback passed to the timeout handler: More invasive; the current approach is cleaner

### D3: Add `durations?: number[]` to the `Task` interface in `context-assembler.ts`

**What:** `Task` interface (line 6–20) gains a new optional field:

```typescript
durations?: number[];  // ms per attempt, most recent last
```

**Why:** Duration tracking requires a persistent field on the Task object. Using an array supports multiple attempts, with index 0 = first attempt, index n = most recent. The field is optional so existing `tasks.json` files without it remain valid.

**Alternatives considered:**
- Storing only the last duration: Loses history needed to show "3 attempts (28m, 15m, 12m)" in the UI
- A separate `TaskDuration` table in SQLite: Overkill; tasks.json is the source of truth for task state

### D4: Record duration at each attempt end in `ralph-executor.ts` around the `_acpSessionRunner` call

**What:** At line 585, before calling `_acpSessionRunner`, record `attemptStartTime = Date.now()`. After the result is handled (line 631+), compute `duration = Date.now() - attemptStartTime` and append to `task.durations`. The `updateTaskInList` helper (line 354–364) is extended to accept `durations` in its `Partial<Pick<Task, ...>>` update payload.

**Why:** Duration must be recorded for both successful and failed attempts (including timeouts). The recording happens in the main for-loop where the attempt result is processed. The actual elapsed time (e.g., 28 minutes) is recorded, not the timeout threshold (e.g., 30 minutes) — this is the wall-clock time from attempt start to result, regardless of outcome.

**Placement:** Duration recording is placed after the result is known (success or failure branch) so the executor always records even if the task fails or times out.

**Alternatives considered:**
- Recording inside `acp-session.ts`: The session runner doesn't have access to the task object or the `updateTaskInList` helper; keeping it in `ralph-executor.ts` is simpler
- Using a finally block: Works for the success path but the failure path has multiple exit points; explicit recording in each branch is clearer

### D5: API endpoints (`/tasks` and `/build-status`) already return tasks directly from `tasks.json` via `fs.readFileSync`

**What:** No structural changes needed to `api/issues.ts`. The tasks are parsed and returned directly. Once `durations` is written to `tasks.json` by `ralph-executor.ts`, it will be included in API responses automatically. The only required change is ensuring the inline type annotation at line 1504 includes `durations?: number[]`.

**Why:** The API reads the file directly and returns it as-is. Since `durations` is additive and optional, existing responses remain valid. The type annotation at line 1504 needs to be updated to include `durations` for correctness, but this is a typing issue only — no runtime behavior changes.

### D6: Frontend type and component changes

**What:**
- `web/src/lib/types.ts`: Add `durations?: number[]` to the `Task` type (there is no existing `Task` type in this file — the existing types relate to Issue, Project, etc. The `ralph_task_update` event type already includes `attempt` but not `durations`. The frontend does not currently have a `Task` interface since it receives tasks via API, not as local types. The type is inferred from the API response.)
- Live task timing: On `ralph_task_update` with status `started`, record local `startTime` in `useSSE.ts`. Use `setInterval` (500ms) to compute elapsed time and display "XXs" in the TaskList. When status becomes `completed` or `failed`, clear the interval.

**Why:** The frontend needs to surface durations received from the API and show live progress for in-progress tasks. The `ralph_task_update` event carries `attempt` but not `durations` — the durations arrive via the API poll (`/tasks` or `/build-status`), not via SSE. Live timing requires client-side tracking since SSE only carries attempt number.

**Alternatives considered:**
- Sending durations via SSE: Adds coupling between executor and SSE; API poll is already in place
- Storing live start time in a React ref: Using a module-level variable in the SSE handler is simpler for this use case

## Risks / Trade-offs

- **[Risk]** `perTaskTimeout` is computed once at loop entry (`ralph-executor.ts:419–421`). Retries do not recalculate the timeout. This is explicitly out of scope, but the behavior may surprise users who expect "3 attempts with longer timeouts on each retry." → No mitigation; documented as non-goal.

- **[Risk]** Adding `durations` to `Task` interface means `tasks.json` schema changes. Existing files without `durations` are unaffected (field is optional), but any code that serializes the entire task object (e.g., `JSON.stringify(task)`) will now include `durations: undefined` if not set. → No mitigation; this is standard additive schema evolution.

- **[Risk]** `onBeforeKill` awaits could add latency to the timeout handling path if the WIP commit is slow (e.g., large diff). However, `onBeforeKill` is only called when the timeout actually fires, and the alternative (misclassifying as plain `timeout`) is worse. → Acceptable trade-off.

## Migration Plan

1. **Phase 1 — Backend core** (files: `ralph-executor.ts`, `acp-session.ts`, `context-assembler.ts`)
   - Change `FAILURE_CATEGORY_CONFIGS.timeout` to `{ maxAttempts: 3, retryable: true }`
   - Fix `onBeforeKill` fire-and-forget in single-round and multi-round sessions
   - Add `durations?: number[]` to `Task` interface
   - Extend `updateTaskInList` to handle `durations` field
   - Add duration recording around `_acpSessionRunner` call

2. **Phase 2 — API and persistence** (files: `api/issues.ts`)
   - Update inline type annotation on line 1504 to include `durations`

3. **Phase 3 — Frontend** (files: `web/src/lib/types.ts`, `web/src/hooks/useSSE.ts`)
   - Add `durations?: number[]` to relevant type (or confirm it's inferred from API)
   - Add local `startTime` tracking on `ralph_task_update` with `status: 'started'`
   - Add `setInterval` for live elapsed time display
   - Clear interval on `completed`/`failed` status

4. **Verification**
   - Run existing test suite to ensure no regressions
   - Manually verify: start a task that times out, confirm it retries up to 3 times, confirm WIP commit success leads to `timeout_with_wip` classification, confirm durations appear in `tasks.json` and API responses

## Open Questions

- Should the frontend show "3 attempts" badge only when `durations.length > 1`, or always when `durations` exists? (Decision: show attempt count in badge always when `durations` has entries, tooltip shows all durations)
- Should the live timer show only for the currently-executing task, or for all in-progress tasks? (Decision: current task only, as other tasks may have `started` event from earlier attempts)