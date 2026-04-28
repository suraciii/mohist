## Context

The frontend IssueDetailPage currently shows a stage progress bar, description, comments, changed files, and an actions sidebar — but no task execution visibility. Meanwhile the backend already serves two REST endpoints (`GET /api/issues/:number/tasks`, `GET /api/issues/:number/build-status`) and two SSE events (`ralph_task_update`, `ralph_loop_progress`) that are already registered in the frontend SSE infrastructure (`useSSE.ts`, `agent-events.ts`). The SSE events are dispatched via `onAgentEvent` but no consumer subscribes to them for UI updates.

Existing frontend patterns to follow:
- API methods in `web/src/lib/api.ts` using the shared `request<T>()` helper
- React Query hooks in `web/src/hooks/useQueries.ts` with `useQuery` + `useMutation`
- SSE consumption via `onAgentEvent` + `useEffect` cleanup (see `useSessionTimeline.ts`)
- Components in `web/src/components/` using Tailwind CSS, following the card pattern (`rounded-lg border border-gray-200 bg-white p-4`)

## Goals / Non-Goals

**Goals:**
- Surface task list and progress on IssueDetailPage from Plan stage onward
- Real-time task status updates via SSE without page refresh
- Show task-level errors inline and dependency blocked hints

**Non-Goals:**
- Task detail expansion (acceptance criteria per-task status)
- DAG dependency visualization
- Task retry/skip interaction controls
- Backend changes of any kind

## Decisions

### D1: Use build-status endpoint as primary data source for TaskList

Use `GET /api/issues/:number/build-status` as the single data source for the TaskList component. This endpoint returns both the `progress` summary and the `tasks` array in one call, avoiding the need to coordinate two separate fetches.

The `getTasks` API method is still added for completeness (it returns the full tasks.json with `description`, `acceptanceCriteria`, `dependsOn` which the build-status endpoint currently omits). But for the initial implementation, `useBuildStatus` drives the TaskList rendering and `useTasks` provides the extended fields like `dependsOn`.

When both queries resolve, the TaskList merges them: build-status provides `tasks[].id, title, passes, attempts, error` and the tasks endpoint supplements with `dependsOn`.

**Alternatives considered:**
- Only use tasks endpoint + compute progress client-side → duplicates backend logic, drift risk
- Only use build-status → missing `dependsOn` field needed for blocked hints; would need backend change
- Chosen: use both, build-status for progress, tasks for dependency data, merge client-side

### D2: SSE updates via queryClient.setQueryData in useTaskProgress hook

Follow the pattern established by `useSessionTimeline.ts`: subscribe to `onAgentEvent('ralph_task_update')` and `onAgentEvent('ralph_loop_progress')`, then update the React Query cache directly with `queryClient.setQueryData`. This gives instant UI re-renders without refetch and works with React Query's built-in stale/refetch logic.

The hook filters events by `event.issueId === String(issueNumber)` to avoid cross-issue cache pollution.

**Alternatives considered:**
- invalidateQueries on every event → triggers refetch storm, defeats SSE purpose
- Separate state outside React Query → loses caching/loading/error handling, dual source of truth
- Chosen: setQueryData for surgical updates, with invalidateQueries as fallback only on stage transitions

### D3: TaskList as a pure presentational component

`TaskList` receives `tasks`, `currentTask`, and `progress` as props. It does not call hooks or fetch data. The parent `IssueDetailPage` orchestrates the data flow via `useBuildStatus`, `useTasks`, and `useTaskProgress`.

This keeps TaskList testable and decoupled from data fetching concerns.

### D4: TaskList insertion point — between Description and Comments

The spec requires TaskList between Description and Comments in the main content column. This is a single insertion at `IssueDetailPage.tsx` around line 260 (after the Description card, before the Comments card).

## Risks / Trade-offs

- **[build-status tasks lack `dependsOn`]** → The backend build-status endpoint returns a simplified task shape without `dependsOn`. Mitigation: fetch both endpoints, merge by task ID. If the tasks endpoint also lacks `dependsOn`, the blocked-by hint simply won't appear (graceful degradation).
- **[SSE cache desync]** → If SSE events arrive before the initial fetch completes, `queryClient.setQueryData` will find no cache entry and skip the update. Mitigation: the hook checks for `undefined` cache and skips; initial fetch will bring fresh data.
- **[Stale data on page revisit]** → React Query's `staleTime` defaults to 0, so cached data is immediately stale. On page revisit, a refetch triggers. Mitigation: acceptable behavior; SSE keeps data fresh while the page is open.

## Migration Plan

No migration needed. Purely additive frontend change. All new files, no existing behavior modified.

Steps:
1. Add types to `types.ts`
2. Add API methods to `api.ts`
3. Add React Query hooks to `useQueries.ts`
4. Create `useTaskProgress.ts` hook
5. Create `TaskList.tsx` component
6. Wire into `IssueDetailPage.tsx`
7. Verify build + typecheck pass

## Open Questions

None — all backend APIs and SSE events are confirmed available.
