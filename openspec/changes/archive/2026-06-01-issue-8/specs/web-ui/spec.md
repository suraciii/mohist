## ADDED Requirements

### Requirement: Issue Detail renders task required files
Issue Detail SHALL render required files declared by workflow tasks as first-class review items on or within the relevant task rows. Required file entries SHALL be visible without requiring users to inspect workflow logs or leave Issue Detail.

#### Scenario: Required files appear on task rows
- **WHEN** Issue Detail renders a workflow task whose API data includes required files
- **THEN** the task row or expandable task section SHALL list each required file path
- **AND** the UI SHALL indicate that the file source is a task expectation
- **AND** marker requirements or missing/unavailable status SHALL be shown when provided by the API

#### Scenario: Tasks without required files remain compact
- **WHEN** Issue Detail renders a workflow task with no required files
- **THEN** the task row SHALL preserve the existing compact task presentation
- **AND** it SHALL NOT show an empty artifact list

### Requirement: Issue Detail opens required file content on demand
Issue Detail SHALL let users open a required task file and inspect its current worktree content in place. The Web UI SHALL fetch content only after the user requests the file and SHALL use the issue-scoped file-content API.

#### Scenario: Open required file content
- **WHEN** a user selects a required file entry from a workflow task
- **THEN** the Web UI SHALL request that file through the scoped issue file-content API
- **AND** it SHALL render the returned content in an in-place viewer or panel
- **AND** the user SHALL remain on Issue Detail

#### Scenario: File content unavailable is visible
- **WHEN** the scoped file-content API reports that a required file is unavailable, missing, or unreadable
- **THEN** the file viewer SHALL show a clear unavailable state for that file
- **AND** it SHALL NOT mark the task itself as failed solely because content could not be loaded for viewing

### Requirement: Board cards show compact stage task progress
Board issue cards SHALL render compact current-stage user-task progress from the issue list read model when meaningful progress exists. The indicator SHALL not crowd card title, labels, or attention states.

#### Scenario: Active card shows progress fraction
- **WHEN** a board card renders an issue whose list item includes `workflowStageProgress` with a non-zero total
- **THEN** the card SHALL show compact progress such as `3/7`
- **AND** the indicator SHALL correspond to the progress stage returned by the API

#### Scenario: Progress respects task classification
- **WHEN** a board card renders progress for a stage containing orchestration/internal tasks
- **THEN** the displayed fraction SHALL count only user-facing tasks reported by the server-side progress summary
- **AND** the UI SHALL NOT recompute progress from hidden timeline data or raw task arrays on the card

#### Scenario: Non-meaningful progress is hidden
- **WHEN** an issue is backlog, done, cancelled, has no user-facing current-stage tasks, or is only waiting for approval/checks
- **THEN** the board card SHALL hide or visually de-emphasize the progress indicator

### Requirement: Task artifact and board progress UI have regression coverage
Web UI task artifact rendering, on-demand file viewing, and board progress indicators SHALL have regression tests that use mocked API responses and fake file-content results.

#### Scenario: Required file rendering is tested
- **WHEN** Web UI tests render Issue Detail with task required file metadata
- **THEN** the tests SHALL verify required file entries are visible on the task surface
- **AND** tasks without required files do not render empty artifact chrome

#### Scenario: File viewer loading is tested with fakes
- **WHEN** Web UI tests select a required file entry
- **THEN** the tests SHALL verify the file-content API is called on demand with issue number and path
- **AND** the tests SHALL use fake API responses rather than real filesystem access

#### Scenario: Board progress rendering is tested
- **WHEN** Web UI tests render board cards with `workflowStageProgress`
- **THEN** the tests SHALL verify compact progress is displayed for active stages
- **AND** progress is hidden or de-emphasized when the API reports no meaningful user-task progress
