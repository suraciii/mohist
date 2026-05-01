## ADDED Requirements

### Requirement: IssueTimeline replaces horizontal stage progress bar
The IssueDetailPage SHALL replace the horizontal stage progress bar with a vertical pipeline timeline positioned below the issue title. The timeline SHALL render pipeline stages (Created, Plan, Approved, Build, Review, Done) in vertical sequence, each showing: stage name, completion status, and elapsed duration.

#### Scenario: Timeline renders completed stages
- **WHEN** the user views an issue with Plan and Build stages completed
- **THEN** the timeline displays: "Created" with timestamp, "Plan" with checkmark and duration (e.g., "8m 26s"), "Approved" with timestamp, "Build" with checkmark and duration (e.g., "6m 10s"), and the current stage "Awaiting review" with pause icon

#### Scenario: Timeline shows pending future stages
- **WHEN** the issue is in Plan stage
- **THEN** stages beyond Plan (Approved, Build, Review, Done) appear as pending nodes with hollow circles and gray text

#### Scenario: Current stage is visually highlighted
- **WHEN** a stage is in progress or awaiting approval
- **THEN** that node is visually emphasized (e.g., filled circle, different color) as the current focus point

### Requirement: IssueTimeline supports three-level collapsible details
The timeline SHALL provide three levels of information disclosure. Default view shows only stage-level summary (stage name, status, duration). Clicking a completed stage expands to show internal structure (Plan rounds or Build tasks). Each expanded section includes a "View session" link.

#### Scenario: Plan stage expands to show rounds
- **WHEN** the user clicks a completed Plan stage node
- **THEN** the node expands to display sub-items: each round label (Proposal, Specs, Design, Tasks, Self-review) with individual duration and status

#### Scenario: Build stage expands to show task list
- **WHEN** the user clicks a completed Build stage node
- **THEN** the node expands to display task items: each task ID and description with completion status (passed/failed/running)

#### Scenario: Expanded section includes View session link
- **WHEN** a stage node is expanded
- **THEN** a "View session →" link appears below the details, linking to the session page for that stage

#### Scenario: Collapsed state is default
- **WHEN** the page loads or the user has not interacted with the timeline
- **THEN** all stages are in collapsed state showing only stage-level summary

### Requirement: IssueTimeline uses RAF throttling for real-time updates
The `useIssueTimeline` hook SHALL implement requestAnimationFrame-based throttling when processing live SSE events to prevent UI lockup during high-frequency streaming.

#### Scenario: SSE events during running issue
- **WHEN** an issue is actively running and `plan_round_start`, `plan_round_complete`, `ralph_task_update`, or `build_started`/`build_completed` events arrive
- **THEN** the timeline batches updates via requestAnimationFrame instead of processing per-event

#### Scenario: High-frequency event burst during Build stage
- **WHEN** 500+ `ralph_task_update` events arrive within 3 seconds
- **THEN** UI remains responsive with updates batched via requestAnimationFrame

### Requirement: useIssueTimeline aggregates data from existing APIs
The `useIssueTimeline` hook SHALL aggregate data from three sources: `useIssue` (for createdAt and approval_state), `useCoderSessions` (for stage sessions), and `GET /api/issues/:number/logs` filtered to plan/build events (for round and task details).

#### Scenario: Timeline reconstructs from multiple API calls
- **WHEN** the hook initializes for an issue
- **THEN** it fetches issue data, coder sessions, and filtered workflow logs
- **AND** constructs a timeline array sorted by timestamp

#### Scenario: Timeline infers stage durations from session start/end
- **WHEN** a Plan session has createdAt "03:12" and completedAt "03:20"
- **THEN** the Plan node displays duration "8m 26s" (or "8m" for simplicity)

### Requirement: IssueTimeline is responsive on mobile
The timeline component SHALL use responsive layout that adapts to smaller screens. On narrow viewports, the vertical timeline SHALL remain functional without horizontal scrolling.

#### Scenario: Mobile viewport renders correctly
- **WHEN** the viewport width is below 768px
- **THEN** the timeline renders without horizontal overflow
- **AND** stage labels and durations remain legible

### Requirement: Timeline shows model used per stage
When a coder session includes model information, the expanded stage details SHALL display the model name.

#### Scenario: Plan stage shows model
- **WHEN** the Plan stage is expanded
- **THEN** "Model: MiniMax-M2.7" (or appropriate model name) appears at the bottom of the expanded section

### Requirement: Timeline displays stage failure states
When a stage fails (e.g., Build fails), the timeline node SHALL display a failure indicator instead of a checkmark.

#### Scenario: Build stage fails
- **WHEN** the Build stage encounters an error and fails
- **THEN** the Build timeline node displays a failure icon (e.g., red ✗) instead of a checkmark
- **AND** the stage remains expandable to show which task(s) failed

#### Scenario: Stage is retried after failure
- **WHEN** a failed Build stage is retried and succeeds
- **THEN** the timeline shows the latest attempt's result (success checkmark with total duration including retry)