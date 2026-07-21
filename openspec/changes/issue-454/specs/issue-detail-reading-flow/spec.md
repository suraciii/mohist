### Requirement: Workflow tasks have one stage-aware home

The issue detail page SHALL present workflow tasks in exactly one task list. The list SHALL show the tasks for the selected workflow stage and SHALL NOT repeat those tasks in a separate progress panel or elsewhere in the reading flow.

#### Scenario: Current stage tasks are rendered once

- **WHEN** an issue has workflow tasks for its current stage
- **THEN** each current-stage task SHALL appear exactly once in the issue detail reading flow
- **AND** the page SHALL NOT render a second task progress list containing the same tasks

#### Scenario: Selected stage changes the task list

- **WHEN** the user selects a different workflow stage
- **THEN** the single task list SHALL show the tasks belonging to that stage
- **AND** tasks from the previously selected stage SHALL no longer occupy that list

### Requirement: Task rows preserve task identity and honest affordances

Every task row SHALL display the task name as its primary text on desktop and phone-width viewports. Artifact paths and runtime metadata SHALL be secondary to the task name. A row SHALL use interactive button or disclosure styling only when activating it reveals task details such as logs, output, failure information, required files, or artifacts; otherwise the row SHALL be presented as non-interactive content.

#### Scenario: Completed task has an artifact on a phone-width viewport

- **WHEN** a completed task with a recorded artifact renders at a phone-width viewport
- **THEN** the row SHALL visibly display the task name as its primary text
- **AND** the artifact path SHALL appear only as secondary information
- **AND** runtime labels and timestamps SHALL NOT displace or hide the task name

#### Scenario: Task row has expandable details

- **WHEN** a task has details that can be revealed
- **THEN** its row SHALL present a visible interactive disclosure affordance
- **AND** activating the row SHALL visibly expand or collapse those details

#### Scenario: Task row has no expandable details

- **WHEN** a task has no details that can be revealed
- **THEN** its row SHALL NOT use button or disclosure styling
- **AND** activating the row SHALL NOT imply an unavailable expansion

### Requirement: Every workflow stage remains reachable

The stage selector SHALL allow the user to select every workflow stage that the page presents, including completed and pending stages whose tasks are available for inspection. At phone width, all stages SHALL remain visibly reachable without requiring the user to discover stages hidden beyond an unindicated horizontal overflow area.

#### Scenario: User inspects a completed stage

- **WHEN** the user selects a completed workflow stage
- **THEN** that stage SHALL become the selected stage
- **AND** the task list SHALL show its tasks

#### Scenario: User inspects stages at phone width

- **WHEN** the issue detail page renders its stage selector at a phone-width viewport
- **THEN** every presented stage SHALL have a reachable selection control
- **AND** selecting any stage SHALL make that stage's tasks inspectable in the single task list

### Requirement: Changes have one summary and one degraded state

The issue detail reading flow SHALL contain exactly one Changes section for branch comparison and changed-file information. When diff information is available, this section SHALL identify the compared branches and preserve the visible scale of the change. When diff information is unavailable, the section SHALL render one degraded-state message that explains what the user cannot inspect as a consequence.

#### Scenario: Diff information is available

- **WHEN** branch comparison and changed-file information are available
- **THEN** exactly one Changes section SHALL identify the compared branches
- **AND** that section SHALL show the file, addition, and deletion counts
- **AND** the page SHALL NOT repeat the same diff summary in a separate banner

#### Scenario: Workspace diff is unavailable

- **WHEN** changed-file information cannot be read because the workspace is unavailable
- **THEN** the Changes section SHALL render the unavailable state exactly once
- **AND** the message SHALL state that the user cannot inspect the changed files or diff
- **AND** the page SHALL NOT concatenate duplicate unavailable messages

### Requirement: Artifacts have one stable reading-flow section

Outside approval-specific inline evidence, the issue detail page SHALL present recorded workflow artifacts in exactly one Artifacts section at a stable position in the reading flow. The page SHALL NOT duplicate that artifact collection in a compact decision-surface opener or another reading-flow panel.

#### Scenario: Recorded artifacts are available

- **WHEN** the current workflow run has recorded artifacts and the issue is not showing approval-specific inline evidence
- **THEN** the issue detail page SHALL render exactly one Artifacts section for those artifacts
- **AND** each artifact in that collection SHALL be opened from that section
- **AND** a duplicate compact artifact collection SHALL NOT appear elsewhere on the page

#### Scenario: Reading flow rerenders across workflow states

- **WHEN** the issue moves between non-approval workflow states
- **THEN** the Artifacts section SHALL retain its established position relative to the other reading-flow sections
