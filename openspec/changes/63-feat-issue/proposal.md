## Why

The Issue Detail page provides zero visibility into task execution progress during the Build stage. Users staring at a "Running" indicator have no idea which task the agent is working on, which ones have passed or failed, or how far along the pipeline is — they can only wait or manually refresh. The backend already exposes tasks data and SSE events (`ralph_task_update`, `ralph_loop_progress`) but the frontend never consumes them.

## What Changes

- Add `getTasks()` and `getBuildStatus()` methods to the frontend API client (`api.ts`)
- Add React Query hooks `useTasks()` and `useBuildStatus()` in `useQueries.ts`
- Add `Task` and `BuildStatus` types to `types.ts`
- Create `TaskList` component displaying task items with status icons (completed/failed/running/pending), progress summary, and blocked-by dependency hints
- Create `useTaskProgress` hook that listens to `ralph_task_update` and `ralph_loop_progress` SSE events via `onAgentEvent` and updates React Query cache in real-time
- Insert `TaskList` into `IssueDetailPage` between Description and Comments sections, visible from Plan stage onward

## Capabilities

### New Capabilities

- `task-list-ui` — TaskList component with status icons, progress bar, dependency hints, and inline error display
- `task-progress-sse` — useTaskProgress hook for real-time SSE-driven task state updates via React Query cache

### Modified Capabilities

## Impact

- **Frontend files**: `api.ts`, `types.ts`, `useQueries.ts`, `IssueDetailPage.tsx` — additions only, no breaking changes
- **New files**: `TaskList.tsx` component, `useTaskProgress.ts` hook
- **Backend**: No changes. Existing `GET /api/issues/:number/tasks`, `GET /api/issues/:number/build-status`, and SSE events (`ralph_task_update`, `ralph_loop_progress`) are already implemented and the SSE event types are already registered in `useSSE.ts` and `agent-events.ts`
- **Dependencies**: None — uses existing React Query, existing SSE infrastructure, existing Tailwind CSS
