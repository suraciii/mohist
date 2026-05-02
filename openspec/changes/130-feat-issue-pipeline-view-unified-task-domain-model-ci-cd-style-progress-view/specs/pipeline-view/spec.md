## ADDED Requirements

### Requirement: PipelineView component replaces fragmented progress components

The IssueDetailPage SHALL include a PipelineView component that replaces IssueTimeline, TaskList, CheckSuitePanel, CheckResultsPanel, and the approval sidebar. PipelineView SHALL render a CI/CD-style visualization composed of a Stage Bar, Step List, and Inline Approval.

#### Scenario: Active issue shows full Pipeline View

- **WHEN** the user navigates to an active issue in Build stage
- **THEN** PipelineView renders a Stage Bar showing Plan (completed), Build (running), Check (pending), Done (pending)
- **AND** below the Stage Bar, a Step List shows Build stage's Tasks and Checks

#### Scenario: Old components no longer imported

- **WHEN** IssueDetailPage source is inspected
- **THEN** it SHALL NOT import IssueTimeline, TaskList, CheckSuitePanel, or CheckResultsPanel
- **AND** those component files SHALL be deleted

### Requirement: Stage Bar shows horizontal pipeline progress

PipelineView SHALL render a horizontal Stage Bar with 4 stages: Plan → Build → Check → Done. Each stage SHALL display a status icon, label, and timing information.

Stage status mapping:
- `completed`: checkmark icon + elapsed duration
- `running`: spinner icon + live elapsed timer
- `failed`: error icon + elapsed duration
- `awaiting`: hourglass icon (user-approval pending)
- `pending`: empty circle icon

#### Scenario: Clicking completed stage shows historical Step List

- **WHEN** the user clicks on the completed Plan stage in the Stage Bar
- **THEN** the Step List below switches to show Plan stage's completed Tasks and Checks
- **AND** the Plan stage bar appears visually selected

#### Scenario: Clicking current stage returns to live view

- **WHEN** the user is viewing historical Plan step list
- **AND** clicks the currently running Build stage in the Stage Bar
- **THEN** the Step List switches back to live Build stage progress

#### Scenario: Stage elapsed timer updates in real-time

- **WHEN** the Build stage is running and has been active for 45 seconds
- **THEN** the Build stage bar shows a spinner icon and "45s" (or formatted duration)
- **AND** the duration increments every second without manual refresh

### Requirement: Step List shows Tasks and Checks for selected stage

Below the Stage Bar, PipelineView SHALL render a Step List divided into two sections: Tasks (Agent-executed work) and Checks (automated verification). The Step List corresponds to the currently selected stage in the Stage Bar.

#### Scenario: Tasks section renders each task with status

- **WHEN** the user views the Step List for a running Build stage with 3 tasks
- **THEN** the Tasks section shows 3 items, each with: status icon + title + duration
- **AND** completed tasks show a checkmark and duration
- **AND** the running task shows a spinner and elapsed time
- **AND** pending tasks show a grey circle

#### Scenario: Completed task can expand to show artifacts

- **WHEN** the user clicks on a completed Plan task "Write Proposal"
- **THEN** the task row expands to show the artifact output (proposal.md content summary or file path)
- **AND** clicking again collapses the detail

#### Scenario: Failed task shows error summary

- **WHEN** a Build task T-002 has status `failed`
- **THEN** the Step List shows T-002 with an error icon and the error message summary
- **AND** the row can be expanded to see full error details

#### Scenario: Checks section renders each check with status

- **WHEN** the Step List shows the Checks section for Plan stage
- **THEN** checks are listed: proposal-complete, specs-complete, design-complete, tasks-valid, self-review-passed, user-approval
- **AND** each shows: status icon (pass/fail/pending) + check name
- **AND** failed checks show the failure reason message

### Requirement: Inline Approval renders in Step List Checks section

When a `user-approval` check is in `awaiting` status, the Step List SHALL render the approval UI inline within the Checks section, replacing any separate approval sidebar.

#### Scenario: Approval UI appears inline when awaiting

- **WHEN** Plan stage completes and the `user-approval` check is awaiting
- **THEN** the user-approval row in the Checks section expands to show:
  - A list of artifact file paths produced by the stage
  - An "Approve" button
  - A "Send back" button with a feedback text input
  - A link to the ChangesPanel for viewing detailed diffs

#### Scenario: User approves inline

- **WHEN** the user clicks "Approve" on the inline approval UI
- **THEN** the approval API is called
- **AND** the Pipeline View transitions to show the next stage as running

#### Scenario: User sends back with feedback

- **WHEN** the user enters feedback text "Change the auth approach" and clicks "Send back"
- **THEN** the reject API is called with the feedback message
- **AND** the Pipeline View shows the current stage as failed

### Requirement: PipelineView handles all issue states

PipelineView SHALL render correctly for all issue statuses: backlog, active, blocked, interrupted, completed, closed.

#### Scenario: Backlog issue shows empty pipeline with Start button

- **WHEN** the user views a backlog issue
- **THEN** the Stage Bar shows 4 pending (grey) stages
- **AND** a "Start" button is displayed below the Stage Bar

#### Scenario: Active issue shows live pipeline

- **WHEN** the user views an active issue in any running stage
- **THEN** the Pipeline View renders the full Stage Bar with current stage highlighted
- **AND** the Step List shows real-time task progress via SSE

#### Scenario: Blocked issue shows failure indicator

- **WHEN** the user views a blocked issue
- **THEN** the current stage in the Stage Bar shows a failed icon
- **AND** the Step List shows the failing task/check with the failure reason

#### Scenario: Interrupted issue shows resume option

- **WHEN** the user views an interrupted issue
- **THEN** the current step shows a lightning bolt icon
- **AND** a "Resume" button is displayed

#### Scenario: Completed issue shows all stages passed

- **WHEN** the user views a completed issue
- **THEN** all 4 stages in the Stage Bar show completed icons
- **AND** the Done stage's Step List can be expanded

#### Scenario: Closed issue shows final state greyed out

- **WHEN** the user views a closed issue
- **THEN** the Pipeline View shows the state at time of closure
- **AND** all Stage Bar elements are greyed out and non-interactive

### Requirement: PipelineView subscribes to stage_task_update SSE events

PipelineView SHALL subscribe to `stage_task_update` SSE events to receive real-time task status changes. When a task status changes, the Step List SHALL update immediately without polling.

#### Scenario: Task transitions from running to completed via SSE

- **WHEN** the user is viewing an active Build stage Step List
- **AND** a `stage_task_update` event arrives with `{ taskId: 'T-001', status: 'completed' }`
- **THEN** T-001's status icon changes from spinner to checkmark
- **AND** T-001's duration is displayed
- **AND** if T-002 is next, it changes from pending to running

#### Scenario: Task retry detected via SSE

- **WHEN** a `stage_task_update` event arrives with `{ taskId: 'T-002', status: 'retrying', attempt: 2 }`
- **THEN** T-002's row shows a retry indicator and the attempt number

### Requirement: PipelineView loads historical data from executions API

When the page loads, PipelineView SHALL fetch historical stage execution data from `GET /api/issues/:number/executions` to populate completed stages' Tasks and Checks without relying on SSE replay.

#### Scenario: Page loads for issue in Build stage

- **WHEN** the user navigates to an issue that has completed Plan and is in Build stage
- **THEN** PipelineView fetches executions data
- **AND** the Plan stage Step List is populated with completed task results from the API response
- **AND** the Build stage Step List shows live SSE data for running tasks

#### Scenario: No executions data for draft issue

- **WHEN** the user views a draft issue with no stage executions
- **THEN** the Stage Bar shows all stages as pending
- **AND** the Step List shows a "Start to begin pipeline" message

### Requirement: Frontend uses RAF throttling for high-frequency SSE events

The `usePipelineView` hook SHALL implement requestAnimationFrame-based throttling for `stage_task_update` and related SSE events to prevent UI lockup during rapid streaming. Events SHALL be buffered in a ref and flushed every 100ms.

#### Scenario: Rapid stage_task_update events during Build stage

- **WHEN** 500+ `stage_task_update` events arrive within 5 seconds
- **THEN** the UI updates in batches (every 100ms) instead of per-event
- **AND** no frame drops occur
