## ADDED Requirements

### Requirement: useTaskProgress hook listens to SSE events for real-time updates

A `useTaskProgress` hook SHALL subscribe to `ralph_task_update` and `ralph_loop_progress` SSE events via `onAgentEvent` from `agent-events.ts`, and update the React Query cache for both `['issues', number, 'tasks']` and `['issues', number, 'build-status']` query keys using `queryClient.setQueryData`.

The hook SHALL accept `issueNumber` as a parameter and filter events by matching `event.issueId === String(issueNumber)`.

#### Scenario: ralph_task_update updates individual task in cache

- **WHEN** `ralph_task_update` event is received with `{ issueId: "5", taskId: "T-003", status: "completed" }`
- **AND** the hook was called with `issueNumber = 5`
- **THEN** the task T-003 in the `['issues', 5, 'tasks']` cache is updated to `passes: true`
- **AND** the `['issues', 5, 'build-status']` cache progress is recalculated

#### Scenario: ralph_task_update with failure updates error in cache

- **WHEN** `ralph_task_update` event is received with `{ issueId: "5", taskId: "T-003", status: "failed", error: "Missing validation" }`
- **THEN** the task T-003 in cache is updated with `passes: false, error: "Missing validation"`

#### Scenario: ralph_task_update with started status marks task as running

- **WHEN** `ralph_task_update` event is received with `{ issueId: "5", taskId: "T-003", status: "started" }`
- **THEN** the task T-003 in cache is updated with `passes: false, error: null`
- **AND** the build-status cache `currentTask` is set to "T-003"

#### Scenario: ralph_loop_progress updates progress counts

- **WHEN** `ralph_loop_progress` event is received with `{ issueId: "5", completed: 3, failed: 1, total: 8 }`
- **THEN** the `['issues', 5, 'build-status']` cache progress is updated to `{ completed: 3, failed: 1, total: 8 }`

#### Scenario: Events for other issues are ignored

- **WHEN** `ralph_task_update` event is received with `{ issueId: "7", taskId: "T-003", status: "completed" }`
- **AND** the hook was called with `issueNumber = 5`
- **THEN** no cache updates are performed

### Requirement: useTaskProgress unsubscribes on unmount

The hook SHALL clean up all event listeners when the component unmounts, using the unsubscribe functions returned by `onAgentEvent`.

#### Scenario: Cleanup on unmount

- **WHEN** the component using `useTaskProgress` unmounts
- **THEN** all `onAgentEvent` listeners registered by the hook are removed
- **AND** no further cache updates occur from this hook instance

### Requirement: useTaskProgress handles missing cache data gracefully

When the target query keys do not exist in the cache (e.g., initial fetch hasn't completed), the hook SHALL skip the update without throwing errors.

#### Scenario: Cache not yet populated

- **WHEN** `ralph_task_update` event is received
- **AND** `queryClient.getQueryData(['issues', 5, 'tasks'])` returns `undefined`
- **THEN** the hook skips the update without error
- **AND** no exception is thrown
