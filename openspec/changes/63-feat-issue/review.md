# Review Report

## Verdict: FAIL

## Dimensions

### Correctness: FAIL

1. **[ERROR] Cache type mismatch in `useTaskProgress.ts:17`** — `setQueryData<Task[]>` is called on key `['issues', issueNumber, 'tasks']`, but the cache stores `{ version: number; tasks: Task[] }` (the full API response from `api.getTasks()`). The callback parameter `old` receives `{ version: number; tasks: Task[] }`, but TypeScript believes it is `Task[]`. Calling `old.map(...)` will throw a runtime TypeError (`old.map is not a function`) because the cached object has no `.map` method. Even if the callback somehow didn't crash, it would overwrite the cache with a bare `Task[]`, breaking `tasksData?.tasks` access in `IssueDetailPage.tsx:69`.

2. **[ERROR] `useTaskProgress.ts:24` sets `error: null` for `status === 'started'`** — When `status === 'started'`, the ternary `event.status === 'failed' ? event.error ?? null : null` correctly clears the error, but when `status === 'retrying'`, the error is also cleared to `null`. This may discard useful error information from a previous attempt if the task is being retried.

3. **[WARNING] `IssueDetailPage.tsx:283` hides TaskList when `mergedTasks.length === 0`** — If the build-status API returns an empty tasks array (e.g., before tasks.json is written during the plan stage), TaskList won't render even if the tasks endpoint returns data. The merge logic on line 67 returns `[]` when `buildStatus?.tasks` is falsy, ignoring `tasksData` entirely. For the Plan stage spec requirement ("tasks.json exists with tasks → show preview"), this means the preview only appears if the build-status endpoint also returns tasks.

### Complexity: PASS

- All functions are concise and focused. `TaskList.tsx` is a clean presentational component at 92 lines. `useTaskProgress.ts` is 76 lines with clear separation between the two event handlers. `IssueDetailPage.tsx` is large (599 lines) but was already large before this change; the additions are proportional.

### Test Coverage: PASS with note

- No new tests were added, but no test infrastructure exists for the web frontend (vitest is not installed in `packages/cli`). The existing test command (`vitest run`) fails with `vitest: not found`. This is a pre-existing gap, not introduced by this change.

### Security: PASS

- No user input is rendered without sanitization beyond what React provides by default. No SQL injection, command injection, or secret exposure risks in the changed code.

### Spec Compliance: FAIL

**T-001 — Types, API methods, React Query hooks**

| Criterion | Result | Notes |
|-----------|--------|-------|
| Task interface has id, title, description?, acceptanceCriteria?, dependsOn?, passes, attempts, error? | PASS | All fields present at `types.ts:253-262` |
| BuildStatus interface has stage, status, progress { completed, failed, total, currentTask }, tasks | PASS | `types.ts:264-274` |
| api.getTasks(5) sends GET /api/issues/5/tasks | PASS | `api.ts:182-183` |
| api.getBuildStatus(5) sends GET /api/issues/5/build-status | PASS | `api.ts:185-186` |
| useTasks(5) registers query with key ['issues', 5, 'tasks'] | PASS | `useQueries.ts:51-57` |
| useBuildStatus(5) registers query with key ['issues', 5, 'build-status'] | PASS | `useQueries.ts:59-65` |
| Typecheck passes | PASS | Confirmed with `tsc --noEmit` |

**T-002 — useTaskProgress hook**

| Criterion | Result | Notes |
|-----------|--------|-------|
| ralph_task_update with status 'completed' sets task passes=true in cache | FAIL | Cache shape mismatch on tasks key — `setQueryData<Task[]>` receives `{ version, tasks }`, causing `.map()` to throw |
| ralph_task_update with status 'failed' sets task error in cache | FAIL | Same root cause as above |
| ralph_task_update with status 'started' clears task error and sets currentTask in build-status cache | PARTIAL | The build-status cache update (line 30-49) is correct. The tasks cache update fails |
| ralph_loop_progress updates completed/failed/total in build-status cache | PASS | Lines 53-69 |
| Events with non-matching issueId are ignored | PASS | `event.issueId !== issueId` guard on lines 15, 55 |
| Listeners are cleaned up on component unmount | PASS | Lines 72-74 |
| Undefined cache is handled gracefully without errors | PASS | `if (!old) return old` guards on lines 18, 31, 58 |
| Typecheck passes | PASS | (TypeScript doesn't catch the semantic mismatch because setQueryData's generic is a type assertion) |

**T-003 — TaskList component**

| Criterion | Result | Notes |
|-----------|--------|-------|
| Returns null when tasks array is empty | PASS | Line 52 |
| Shows 'Tasks' header with 'X/Y completed' progress summary | PASS | Lines 56-61 |
| Completed tasks (passes=true) show green check icon | PASS | Lines 10-15, green-500 SVG |
| Failed tasks (passes=false && error) show red X icon with inline error text below title | PASS | Lines 18-23, red-500 SVG; error shown at line 78-81 with `text-red-500` |
| Running task (matches currentTask) shows blue pulsing dot | PASS | Line 27, `bg-blue-500 animate-pulse` |
| Pending tasks (passes=false, no error, not current) show gray circle | PASS | Line 30, `border-gray-300` hollow circle |
| Tasks with unmet dependsOn show 'blocked by T-xxx' hint | PASS | Lines 33-49, shows `blocked by T-001, T-002` |
| Tasks with all dependsOn met or no dependsOn show no hint | PASS | Line 34 short-circuits |
| Uses existing Tailwind card styling pattern | PASS | `rounded-lg border border-gray-200 bg-white p-4` |
| Typecheck passes | PASS | |

**T-004 — Wire TaskList into IssueDetailPage**

| Criterion | Result | Notes |
|-----------|--------|-------|
| TaskList appears between Description and Comments sections | PASS | Lines 283-293, after Description (line 281) and before Comments (line 295) |
| TaskList is visible when issue stage is plan, build, review, or done | PASS | `TASK_LIST_STAGES` on line 29 |
| TaskList is hidden when issue stage is draft or explore | PASS | Not in TASK_LIST_STAGES set |
| useTaskProgress(issueNumber) is called for SSE real-time updates | PASS | Line 64 |
| Tasks from build-status are merged with tasks endpoint data (dependsOn supplemented) | PASS | Lines 66-78 |
| Progress summary reflects correct completed/total counts | PASS | Lines 287-291 |
| No existing functionality is broken | PASS | Stage bar, comments, actions all intact |
| Web UI builds without errors | PASS | `npm run build` succeeds |
| Typecheck passes | PASS | |

## Fix Suggestions

1. **`useTaskProgress.ts:17`** — Change the tasks cache update to match the actual stored shape. The cache key `['issues', issueNumber, 'tasks']` stores `{ version: number; tasks: Task[] }`, not `Task[]`. Fix:

   ```typescript
   queryClient.setQueryData<{ version: number; tasks: Task[] }>(['issues', issueNumber, 'tasks'], (old) => {
     if (!old) return old
     return {
       ...old,
       tasks: old.tasks.map((task) => {
         if (task.id !== event.taskId) return task
         return {
           ...task,
           passes: event.status === 'completed',
           error: event.status === 'failed' ? event.error ?? null : null,
           attempts: event.attempt ?? task.attempts,
         }
       }),
     }
   })
   ```

2. **`useTaskProgress.ts:24`** — For `status === 'retrying'`, consider preserving the existing error instead of clearing it to `null`, since a retry implies a prior failure with error context that may still be relevant to display.
