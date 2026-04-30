## ADDED Requirements

### Requirement: Check Results Panel replaces ReviewApprovalPanel

The Issue Detail Page SHALL display a Check Results Panel when the issue is in Check stage and `approvalState.status` is `'awaiting'`. The panel SHALL render one row per check in the `CheckSuiteOutput`, showing check name, status indicator (pending/running/passed/failed), duration, and expandable details.

#### Scenario: All checks passed — green panel with approve button
- **WHEN** all `CheckResult` entries have `status: 'passed'`
- **THEN** panel shows three rows (Build & Test, Merge Ready, AI Review) with green passed indicators
- **AND** displays an "Approve & Merge" button
- **AND** clicking "Approve & Merge" calls `POST /api/issues/:number/approve`

#### Scenario: Build & Test failed — red panel with rollback only
- **WHEN** Build & Test `CheckResult.status` is `'failed'`
- **THEN** Build & Test row shows red failed indicator with expandable build log
- **AND** Merge Ready and AI Review rows show `pending` (not run)
- **AND** only a "Back to Build" button is available
- **AND** "Approve & Merge" button is NOT displayed

#### Scenario: AI Review failed — amber panel with three actions
- **WHEN** AI Code Review `CheckResult.status` is `'failed'`
- **AND** Build & Test is `'passed'`
- **THEN** AI Review row shows amber failed indicator with expandable review report
- **AND** three action buttons are displayed: "Back to Build" (退回去修), "Add Instructions" (添加指令), "Force Approve" (强行批准)

#### Scenario: Merge Ready needs rebase — informational badge
- **WHEN** Merge Ready `CheckResult.summary` contains "needs rebase"
- **AND** `CheckResult.status` is `'passed'`
- **THEN** Merge Ready row shows a blue informational badge "Needs Rebase"
- **AND** the badge is informational only, not blocking
- **AND** approve button is still available if all other checks pass

### Requirement: Check status real-time updates via SSE

The Check Results Panel SHALL update check statuses in real-time as the check suite progresses, using SSE events emitted by the workflow controller.

#### Scenario: Check starts running
- **WHEN** a check transitions from `pending` to `running`
- **THEN** the corresponding row shows a loading spinner
- **AND** other pending rows remain in pending state

#### Scenario: Check completes during viewing
- **WHEN** a check transitions from `running` to `passed` or `failed`
- **AND** the user is viewing the issue detail page
- **THEN** the row updates to show the completed status without page refresh

#### Scenario: Auto-fix in progress
- **WHEN** Build & Test check fails and auto-fix starts
- **THEN** Build & Test row shows "Auto-fixing..." indicator with attempt count (e.g., "Attempt 1/2")
- **AND** indicator updates when auto-fix completes (pass or fail)

### Requirement: Expandable check details

Each check row SHALL be expandable to show check-specific details: build/test output for Build & Test, merge readiness info for Merge Ready, and full review report for AI Code Review.

#### Scenario: Expand Build & Test details
- **WHEN** user clicks on Build & Test check row
- **THEN** panel expands to show `buildLog` content (truncated if large, with "Show full log" option)
- **AND** if `autoFixed` is true, shows "Auto-fixed" badge with before/after summary

#### Scenario: Expand AI Review details
- **WHEN** user clicks on AI Review check row
- **THEN** panel expands to show the full `reviewReport` markdown
- **AND** renders markdown content with proper formatting

#### Scenario: Expand Merge Ready details
- **WHEN** user clicks on Merge Ready check row
- **THEN** panel expands to show merge readiness status and any conflict file list if applicable

### Requirement: Add Instructions action injects message

The "Add Instructions" action SHALL allow the user to type a message that is injected into the agent session, triggering a retry of the AI Code Review check.

#### Scenario: User adds instructions
- **WHEN** user clicks "Add Instructions" button
- **THEN** a text input appears for the user to type instructions
- **AND** on submit, calls `POST /api/issues/:number/messages` with the instructions
- **AND** agent resumes and re-runs the AI Code Review check

#### Scenario: User force approves despite AI Review failure
- **WHEN** user clicks "Force Approve" button
- **THEN** system calls `POST /api/issues/:number/approve` with a force flag
- **AND** approval proceeds despite AI Review failure
- **AND** `MergeQueue.enqueue()` is called

### Requirement: Back to Build action regresses stage

The "Back to Build" action SHALL transition the issue back to Build stage, preserving the check results for reference.

#### Scenario: User sends back to Build from check failure
- **WHEN** user clicks "Back to Build" button
- **THEN** issue stage transitions from `check` to `build`
- **AND** check results are cleared from `approvalState.output`
- **AND** agent restarts from Build stage
