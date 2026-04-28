## ADDED Requirements

### Requirement: useSessionTimeline subscribes to ralph_task_update and ralph_loop_progress
The `useSessionTimeline` hook SHALL subscribe to `ralph_task_update` and `ralph_loop_progress` SSE events and maintain a `TaskProgress` state map keyed by `taskId`. The hook SHALL expose this state via its return value.

#### Scenario: Build stage task starts
- **WHEN** a `ralph_task_update` SSE event arrives with `status: 'started'` for task `T-001`
- **THEN** `useSessionTimeline` adds/updates the entry for `T-001` with `status: 'running'`, `taskIndex`, `totalTasks`, and `executionId`

#### Scenario: Build stage task completes
- **WHEN** a `ralph_task_update` SSE event arrives with `status: 'completed'` for task `T-001`
- **THEN** `useSessionTimeline` updates the entry for `T-001` to `status: 'passed'` and retains the `taskIndex` and `totalTasks`

#### Scenario: Build stage task fails
- **WHEN** a `ralph_task_update` SSE event arrives with `status: 'failed'` for task `T-003` with `error: 'Missing backend validation'` and `attempt: 3`
- **THEN** `useSessionTimeline` updates the entry for `T-003` to `status: 'failed'` with `error` and `attempt` fields

#### Scenario: Build stage task retrying
- **WHEN** a `ralph_task_update` SSE event arrives with `status: 'retrying'` for task `T-003` with `attempt: 2`
- **THEN** `useSessionTimeline` updates the entry for `T-003` to `status: 'retrying'` with the `attempt` value

#### Scenario: Loop progress updates
- **WHEN** a `ralph_loop_progress` SSE event arrives with `completed: 2, failed: 0, total: 5`
- **THEN** `useSessionTimeline` exposes a loop progress object with `{ completed: 2, failed: 0, total: 5 }`

#### Scenario: Events filtered by issueId
- **WHEN** a `ralph_task_update` event arrives with `issueId` not matching the current `issueNumber`
- **THEN** the event is ignored

### Requirement: TaskProgressPanel renders Build stage task list
The SessionTimeline component SHALL include a `TaskProgressPanel` that renders during Build stage. The panel SHALL display each task with its ID, status indicator, and overall progress summary. The panel SHALL be visible above the round sections when the issue is in Build stage.

#### Scenario: Build stage with 3 passed and 1 running
- **WHEN** the issue is in Build stage and task progress contains T-001 (passed), T-002 (passed), T-003 (passed), T-004 (running)
- **THEN** TaskProgressPanel renders a summary line "3/4 tasks passed" and a list of 4 task entries, each showing the task ID with a colored status icon (green check for passed, blue spinner for running)

#### Scenario: Build stage with a failed task
- **WHEN** the issue is in Build stage and task progress contains T-001 (passed), T-002 (failed, error: "Cannot find module")
- **THEN** TaskProgressPanel shows "1/2 tasks passed" with T-001 having a green check and T-002 having a red X with the error message

#### Scenario: Non-build stage hides TaskProgressPanel
- **WHEN** the issue is in Plan, Review, or Done stage
- **THEN** TaskProgressPanel is not rendered

#### Scenario: Live streaming during Build stage
- **WHEN** the issue is in Build stage and `isStreaming` is true
- **THEN** TaskProgressPanel updates in real-time as `ralph_task_update` events arrive, without full re-renders of other timeline components

### Requirement: TaskProgress state types
The frontend types SHALL define a `TaskProgressEntry` type and a `TaskProgressMap` type:

```typescript
interface TaskProgressEntry {
  taskId: string
  taskIndex: number
  totalTasks: number
  status: 'pending' | 'running' | 'passed' | 'failed' | 'retrying'
  executionId?: string
  attempt?: number
  error?: string
}

type TaskProgressMap = Map<string, TaskProgressEntry>

interface LoopProgress {
  completed: number
  failed: number
  total: number
}
```

#### Scenario: TaskProgressEntry used in hook return
- **WHEN** `useSessionTimeline` returns task progress data
- **THEN** each entry conforms to the `TaskProgressEntry` interface with all required fields populated from the SSE event payload
