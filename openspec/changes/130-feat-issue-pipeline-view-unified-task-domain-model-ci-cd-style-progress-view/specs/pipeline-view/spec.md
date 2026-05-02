## ADDED Requirements

### Requirement: PipelineView replaces fragmented issue progress components

The IssueDetailPage SHALL use a single `PipelineView` component to display issue progress, replacing IssueTimeline, TaskList, CheckSuitePanel, CheckResultsPanel, Review Report sidebar, and Approval Required sidebar. These replaced components SHALL be deleted from the codebase.

#### Scenario: IssueDetailPage uses PipelineView

- **WHEN** the IssueDetailPage source is inspected
- **THEN** `PipelineView` is imported and rendered in place of IssueTimeline, TaskList, CheckSuitePanel, and CheckResultsPanel
- **AND** no imports of IssueTimeline, TaskList, CheckSuitePanel, or CheckResultsPanel exist

#### Scenario: Deleted components do not exist

- **WHEN** the component directory is inspected
- **THEN** `IssueTimeline.tsx`, `TaskList.tsx`, `CheckSuitePanel.tsx`, and `CheckResultsPanel.tsx` do not exist

### Requirement: Stage Bar displays pipeline stages horizontally

PipelineView SHALL render a horizontal Stage Bar showing Plan → Build → Check → Done. Each stage cell SHALL display:
- Stage name
- Status icon with color coding
- Duration (for completed/running stages)

Status icon mapping:
| Status | Icon | Meaning |
|--------|------|---------|
| completed | checkmark + duration | All checks passed |
| running | spinner + elapsed | Currently executing |
| failed | cross + duration | Check failed, reaction unresolved |
| awaiting-approval | hourglass | User-approval pending |
| pending | empty circle | Not yet reached |

#### Scenario: Active issue in Build stage

- **WHEN** the user views an issue in Build stage
- **THEN** Stage Bar shows: Plan (completed checkmark + duration) → Build (running spinner + elapsed) → Check (pending circle) → Done (pending circle)

#### Scenario: Issue awaiting approval after Plan

- **WHEN** Plan stage completed and user-approval check is pending
- **THEN** Stage Bar shows: Plan (hourglass) → Build (pending) → Check (pending) → Done (pending)

#### Scenario: All stages completed

- **WHEN** the issue is in Done stage with all stages passed
- **THEN** Stage Bar shows: Plan (checkmark + duration) → Build (checkmark + duration) → Check (checkmark + duration) → Done (checkmark)

### Requirement: Stage Bar click selects stage for Step List

Clicking a stage cell in the Stage Bar SHALL select that stage for display in the Step List below. The currently active (running) stage SHALL be selected by default. Clicking a completed stage shows historical task and check results.

#### Scenario: Default selection is active stage

- **WHEN** the user views an issue in Build stage
- **THEN** Build is selected in the Stage Bar and the Step List shows Build's tasks and checks

#### Scenario: Click completed Plan stage

- **WHEN** the user clicks the Plan stage cell in the Stage Bar
- **THEN** the Step List switches to show Plan's completed tasks and checks
- **AND** clicking Build again switches back to the current stage

### Requirement: Step List shows Tasks and Checks sections

Below the Stage Bar, PipelineView SHALL render a Step List for the selected stage, divided into two sections: "Tasks" (Agent work) and "Checks" (validation).

#### Scenario: Plan stage Step List

- **WHEN** Plan stage is selected in the Stage Bar
- **THEN** Tasks section shows 5 items: Write Proposal, Write Specs, Write Design, Break into Tasks, Self-Review
- **AND** Checks section shows: proposal-complete, specs-complete, design-complete, tasks-valid, self-review-passed, user-approval

#### Scenario: Build stage Step List

- **WHEN** Build stage is selected in the Stage Bar
- **THEN** Tasks section shows tasks from tasks.json (e.g., T-001, T-002, T-003)
- **AND** Checks section shows: all-tasks-complete, code-compiles

#### Scenario: Check stage Step List

- **WHEN** Check stage is selected in the Stage Bar
- **THEN** Tasks section shows 2 items: AI Code Review, Review Self-Check
- **AND** Checks section shows: build-test, ai-review, user-approval

### Requirement: Task items display status, title, and timing

Each Task item in the Step List SHALL display:
- Status icon (completed: checkmark, running: spinner, failed: cross, pending: empty circle)
- Task title
- Duration for completed/running tasks

#### Scenario: Completed task is expandable

- **WHEN** a task with `status: 'completed'` is rendered
- **THEN** the task row shows a checkmark icon, task title, and duration
- **AND** clicking the row expands to show the artifact list

#### Scenario: Running task shows live progress

- **WHEN** a task with `status: 'running'` is rendered
- **THEN** the task row shows a spinner icon, task title, and elapsed time updating in real-time

#### Scenario: Failed task shows error

- **WHEN** a task with `status: 'failed'` is rendered
- **THEN** the task row shows a cross icon, task title, and failure summary
- **AND** expanding shows the error details

#### Scenario: Pending task is dimmed

- **WHEN** a task with `status: 'pending'` is rendered
- **THEN** the task row is styled in a muted/dimmed color
- **AND** shows an empty circle icon

### Requirement: Check items display status and result

Each Check item in the Step List SHALL display:
- Status icon (pass: checkmark, fail: cross, pending: empty circle)
- Check name
- Result message (for failed checks)

#### Scenario: Passed check

- **WHEN** a check with `status: 'pass'` is rendered
- **THEN** the check row shows a checkmark icon and check name

#### Scenario: Failed check with reason

- **WHEN** a check with `status: 'fail'` is rendered
- **THEN** the check row shows a cross icon, check name, and failure message
- **AND** if the check has a reaction in progress (retry/auto-fix), the reaction status is shown

#### Scenario: Pending check

- **WHEN** a check with `status: 'pending'` is rendered
- **THEN** the check row shows an empty circle icon and check name in muted style

### Requirement: Inline Approval renders in Step List

When a `user-approval` check is pending (status: fail with ask-user reaction), the PipelineView SHALL render an inline approval panel within the Checks section instead of a separate sidebar panel. The inline panel SHALL display:
- List of artifacts produced by the current stage
- "Approve" button and "Send back" button
- Optional feedback text input
- Link to the ChangesPanel for detailed file-level review

#### Scenario: Plan stage awaiting approval

- **WHEN** Plan stage is selected and user-approval check is pending
- **THEN** the Checks section shows the user-approval check with an inline panel
- **AND** the panel lists Plan artifacts: proposal.md, specs/, design.md, tasks.json, self-review.md
- **AND** Approve and Send back buttons are visible

#### Scenario: User approves inline

- **WHEN** the user clicks "Approve" in the inline approval panel
- **THEN** the approval is submitted via the existing approval API
- **AND** the PipelineView updates to show the check as passed

#### Scenario: User sends back with feedback

- **WHEN** the user enters "Fix the error handling" in the feedback input and clicks "Send back"
- **THEN** the rejection is submitted via the existing reject API with the feedback message
- **AND** the pipeline reacts according to the check's reaction configuration

### Requirement: PipelineView handles special issue states

PipelineView SHALL render appropriately for each issue status:

| Issue Status | Pipeline View |
|-------------|---------------|
| backlog | All 4 stages show pending (empty circles) with a "Start" button |
| active | Normal pipeline view with current stage highlighted |
| blocked | Current stage shows failed icon and failure reason banner |
| interrupted | Current task shows interrupted icon with a "Resume" button |
| completed | All stages show completed with Done expanded |
| closed | Pipeline frozen at pre-close state, all cells non-interactive (dimmed) |

#### Scenario: Backlog issue shows Start button

- **WHEN** the user views a backlog issue
- **THEN** Stage Bar shows 4 pending (empty circle) stages
- **AND** a "Start" button is displayed below the Stage Bar
- **AND** clicking "Start" triggers the existing start API

#### Scenario: Blocked issue shows failure

- **WHEN** the user views a blocked issue (Build stage failed)
- **THEN** Stage Bar shows Build with a failed icon
- **AND** a banner displays the failure reason
- **AND** the Step List shows the failed check with error details

#### Scenario: Interrupted issue shows Resume

- **WHEN** the user views an interrupted issue
- **THEN** the current task shows an interrupted icon
- **AND** a "Resume" button is displayed

#### Scenario: Completed issue shows all green

- **WHEN** the user views a completed issue
- **THEN** all stages show checkmarks with durations
- **AND** Done is selected by default in the Stage Bar

#### Scenario: Closed issue is read-only

- **WHEN** the user views a closed issue
- **THEN** the PipelineView shows the state at time of closure
- **AND** all interactive elements (buttons, expandable rows) are disabled/dimmed

### Requirement: PipelineView updates in real-time via SSE

PipelineView SHALL subscribe to `stage_task_update` SSE events for the current issue and update task statuses in real-time without requiring a full page refresh. Running task elapsed times SHALL update continuously.

#### Scenario: Task starts while viewing

- **WHEN** the user is viewing the issue page and a `stage_task_update` with `status: 'started'` arrives
- **THEN** the corresponding task in the Step List changes from pending to running with a spinner

#### Scenario: Task completes while viewing

- **WHEN** the user is viewing and a `stage_task_update` with `status: 'completed'` arrives
- **THEN** the task status updates to completed with a checkmark and final duration

#### Scenario: Stage transitions update Stage Bar

- **WHEN** the current stage completes and the next stage begins
- **THEN** the Stage Bar updates to reflect the new stage states
- **AND** the Step List switches to the new active stage
