## ADDED Requirements

### Requirement: Frontend types for Task and BuildStatus

`types.ts` SHALL define `Task` and `BuildStatus` interfaces matching the backend response shapes.

`Task` SHALL include: `id`, `title`, `description` (optional), `acceptanceCriteria` (optional string array), `dependsOn` (optional string array), `passes`, `attempts`, `error` (optional string).

`BuildStatus` SHALL include: `stage` (string), `status` (string), `progress` with `{ completed: number, failed: number, total: number, currentTask: string | null }`, `tasks` (Task array).

#### Scenario: Task type fields

- **WHEN** `types.ts` is inspected
- **THEN** a `Task` interface exists with fields `id`, `title`, `description?`, `acceptanceCriteria?`, `dependsOn?`, `passes`, `attempts`, `error?`
- **AND** all field types match the backend `tasks.json` schema

#### Scenario: BuildStatus type fields

- **WHEN** `types.ts` is inspected
- **THEN** a `BuildStatus` interface exists with fields `stage`, `status`, `progress` (containing `completed`, `failed`, `total`, `currentTask`), `tasks`

### Requirement: API client methods for tasks and build-status

`api.ts` SHALL add `getTasks(number)` and `getBuildStatus(number)` methods that call `GET /api/issues/:number/tasks` and `GET /api/issues/:number/build-status` respectively.

#### Scenario: getTasks returns task list

- **WHEN** `api.getTasks(5)` is called
- **THEN** a GET request is sent to `/api/issues/5/tasks`
- **AND** the response data (containing `{ version, tasks }`) is returned

#### Scenario: getBuildStatus returns build progress

- **WHEN** `api.getBuildStatus(5)` is called
- **THEN** a GET request is sent to `/api/issues/5/build-status`
- **AND** the response data (containing `{ stage, status, progress, tasks }`) is returned

### Requirement: React Query hooks for tasks and build-status

`useQueries.ts` SHALL export `useTasks(number)` and `useBuildStatus(number)` hooks.

#### Scenario: useTasks hook

- **WHEN** component calls `useTasks(5)`
- **THEN** a query is registered with key `['issues', 5, 'tasks']`
- **AND** the query calls `api.getTasks(5)`
- **AND** the query is enabled only when `number > 0`

#### Scenario: useBuildStatus hook

- **WHEN** component calls `useBuildStatus(5)`
- **THEN** a query is registered with key `['issues', 5, 'build-status']`
- **AND** the query calls `api.getBuildStatus(5)`
- **AND** the query is enabled only when `number > 0`

### Requirement: TaskList component renders task items with status indicators

`TaskList` component SHALL accept a `tasks` array and render each task with a status-specific visual indicator:
- `passes === true` → green check icon (completed)
- `passes === false && error` → red X icon (failed), with error message displayed inline below the task title
- `passes === false && !error` and task is the current running task → blue pulsing dot (running)
- `passes === false` and not running → gray circle (pending)

#### Scenario: Completed task display

- **WHEN** TaskList renders a task with `passes: true`
- **THEN** the task row shows a green check icon
- **AND** the task title is displayed with completed styling

#### Scenario: Failed task display with error

- **WHEN** TaskList renders a task with `passes: false, error: "Missing backend validation"`
- **THEN** the task row shows a red X icon
- **AND** the task title is displayed
- **AND** the error message "Missing backend validation" is shown inline below the title in red text

#### Scenario: Running task display

- **WHEN** TaskList renders a task with `passes: false, error: null` that matches `currentTask`
- **THEN** the task row shows a blue pulsing dot icon

#### Scenario: Pending task display

- **WHEN** TaskList renders a task with `passes: false, error: null` that does NOT match `currentTask`
- **THEN** the task row shows a gray circle icon

### Requirement: TaskList shows dependency blocked hints

When a task has `dependsOn` entries that reference tasks which have `passes === false`, the TaskList SHALL display a "blocked by T-xxx" hint below the task title.

#### Scenario: Task blocked by unfinished dependency

- **WHEN** task T-003 has `dependsOn: ["T-001", "T-002"]`
- **AND** T-001 has `passes: true` but T-002 has `passes: false`
- **THEN** T-003 row shows "blocked by T-002" hint text

#### Scenario: Task with all dependencies met

- **WHEN** task T-003 has `dependsOn: ["T-001", "T-002"]`
- **AND** both T-001 and T-002 have `passes: true`
- **THEN** T-003 row does NOT show any "blocked by" hint

#### Scenario: Task with no dependsOn

- **WHEN** a task has no `dependsOn` field or an empty array
- **THEN** no "blocked by" hint is shown

### Requirement: TaskList shows overall progress summary

TaskList SHALL display a progress summary line (e.g., "5/8 completed") above the task list.

#### Scenario: Progress summary with completed tasks

- **WHEN** TaskList renders with 8 tasks, 5 of which have `passes: true`
- **THEN** a header shows "Tasks" with a summary like "5/8 completed"

#### Scenario: Progress summary with zero tasks

- **WHEN** TaskList receives an empty tasks array
- **THEN** the component renders nothing (returns null)

### Requirement: TaskList is embedded in IssueDetailPage

`IssueDetailPage` SHALL render the `TaskList` component between the Description section and the Comments section. The component SHALL be visible when the issue stage is `plan`, `build`, `review`, or `done`.

#### Scenario: TaskList visible during Build stage

- **WHEN** user views an issue with stage `build`
- **THEN** the TaskList panel appears between Description and Comments
- **AND** shows current task progress

#### Scenario: TaskList visible during Plan stage

- **WHEN** user views an issue with stage `plan`
- **AND** tasks.json exists with tasks
- **THEN** the TaskList panel appears showing the task list preview

#### Scenario: TaskList hidden during Draft stage

- **WHEN** user views an issue with stage `draft`
- **THEN** the TaskList panel is not rendered
