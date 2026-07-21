### Requirement: Issue status has one authoritative presentation

The issue detail page SHALL state the current issue and, when one exists, workflow status exactly once in the sticky status headline. The headline SHALL include the current task when one exists, and the page SHALL NOT repeat the runtime summary in the issue header, repeat the current task as a second pill, or repeat Issue Stage and Workflow Stage in the Details section. A composite parent without a workflow decision SHALL receive an issue-only status statement and SHALL NOT fabricate a workflow status or current task.

#### Scenario: Running issue has one status statement

- **WHEN** an owner opens an issue with a running workflow and a current task
- **THEN** the sticky status headline SHALL present the running summary and current task
- **AND** no other header badge, task pill, or Details row SHALL repeat that status information

#### Scenario: Non-running issue has one status statement

- **WHEN** an owner opens an issue that is waiting, blocked, failed, paused, or done
- **THEN** its runtime summary SHALL appear exactly once in the sticky status headline

#### Scenario: Composite parent has an issue-only status statement

- **WHEN** an owner opens a composite parent that has no workflow decision
- **THEN** the sticky status headline SHALL present its issue status exactly once
- **AND** the page SHALL NOT present a workflow status or current task for that parent

### Requirement: Decision context explains the current state

The decision surface SHALL present a plain-language rationale for the current state and the next expected action or outcome. It MUST distinguish a workflow paused for an approval decision from a workflow that was manually stopped, and approval copy MUST NOT assume that the person viewing the page is the designated approver.

#### Scenario: Approval pause is identified

- **WHEN** a workflow is paused while awaiting approval
- **THEN** the decision context SHALL say that an approval decision is pending
- **AND** it SHALL NOT describe the workflow as manually stopped or assume the viewer must decide

#### Scenario: Manually stopped workflow is identified

- **WHEN** a workflow is paused because execution was manually stopped
- **THEN** the decision context SHALL explain that execution was stopped and state the available next step
- **AND** it SHALL NOT describe the state as awaiting approval

### Requirement: Issue actions use one decision surface

The issue decision surface SHALL be the sole action location for approve, send back, retry, resume, rerun stage, stop, start, mark ready, close, mark as done, and ask agent whenever those actions apply to the current issue state. Workflow action availability SHALL come from the existing runtime decision and server-authorized rules; issue lifecycle action applicability SHALL come from the current Issue facts and existing lifecycle rules. The page SHALL NOT render a separate rail Actions card or a second approve/request-changes implementation inside the workflow view, and consolidation MUST NOT introduce a new lifecycle action.

#### Scenario: Approval actions are not duplicated

- **WHEN** an issue is awaiting an approval decision
- **THEN** approve and send back SHALL be available through the decision surface when authorized
- **AND** the workflow view SHALL NOT render another approve or request-changes control

#### Scenario: Lifecycle and delegation actions share the surface

- **WHEN** mark ready, close, mark as done, or ask agent applies to an issue
- **THEN** each applicable action SHALL be reachable from the decision surface
- **AND** a separate rail Actions card SHALL NOT be rendered

#### Scenario: Unauthorized action is not enabled by consolidation

- **WHEN** the current runtime decision or issue state does not authorize an action
- **THEN** consolidating the page SHALL NOT make that action executable

#### Scenario: Composite parent retains applicable lifecycle actions

- **WHEN** a composite parent has no workflow decision and an issue lifecycle or delegation action applies
- **THEN** that action SHALL be reachable from the issue decision surface
- **AND** the surface SHALL NOT offer a workflow action for the parent

### Requirement: Narrow viewports preserve the complete decision

On a narrow phone viewport, the page SHALL expose every action applicable to the current issue state, not only the primary action. The rationale and next-action text SHALL also be present and readable on the page or within the expanded action control.

#### Scenario: Approval alternatives are reachable on a phone

- **WHEN** an owner opens an issue awaiting approval on a narrow viewport
- **THEN** both approve and send back SHALL be reachable from the mobile decision controls when authorized
- **AND** the rationale and next-action text SHALL be readable

#### Scenario: Secondary actions are reachable on a phone

- **WHEN** the current state has more than one applicable action on a narrow viewport
- **THEN** the mobile decision controls SHALL provide access to the complete applicable action list

### Requirement: Unavailable actions are honest and accessible

Every action shown but unavailable SHALL display a visible, accessible reason in product language and SHALL be visually unmistakable as disabled. A reason MUST be readable without inspecting the DOM and MUST NOT contain implementation terms including "projection", "surface", or "backend". When an action is temporarily unavailable because its request is pending, its progress label and associated reason SHALL identify the operation in progress and that another request cannot be made until it finishes. These rules SHALL apply across desktop and narrow viewports, including disabled Stop and Rebase controls.

#### Scenario: Disabled runtime action explains its availability

- **WHEN** Stop or another runtime action is shown but cannot currently be taken
- **THEN** the page SHALL visibly explain when or why the action becomes available
- **AND** the control SHALL look disabled rather than actionable

#### Scenario: Disabled branch action is visibly unavailable

- **WHEN** Rebase is shown but cannot currently be requested
- **THEN** its plain-language unavailability reason SHALL be visible and accessible
- **AND** the Rebase control SHALL look disabled rather than actionable

#### Scenario: Disabled copy uses product language

- **WHEN** any unavailable action reason is displayed
- **THEN** the reason SHALL describe the user's situation or required next condition
- **AND** it MUST NOT contain the terms "projection", "surface", or "backend"

#### Scenario: Pending action explains its temporary disabled state

- **WHEN** a workflow or issue lifecycle action is disabled because its request is pending
- **THEN** the action SHALL visibly identify the operation in progress
- **AND** an accessible associated reason SHALL explain that another request is unavailable until the operation finishes
- **AND** the control SHALL look disabled on desktop and narrow viewports

### Requirement: The decision surface never presents an unexplained dead end

For a running issue, the decision surface SHALL provide at least one reachable relevant action or clearly explain in product language why no action can currently be taken. It MUST NOT present an action set in which every control is disabled without a visible explanation.

#### Scenario: Running issue has no currently executable action

- **WHEN** a running issue has no action that can currently be executed
- **THEN** the decision surface SHALL explain why no action is available and what condition must change next

### Requirement: Transcript actions lead to an existing session

The page SHALL NOT render a permanently disabled View transcript action. When an execution session exists, the decision surface SHALL offer a working transcript action for that session; when no session exists, it SHALL omit transcript navigation rather than show a dead control.

#### Scenario: Session transcript is available

- **WHEN** the issue has an execution session
- **THEN** the decision surface SHALL offer an enabled transcript action that opens that session's transcript

#### Scenario: No session transcript exists

- **WHEN** the issue has no execution session
- **THEN** the decision surface SHALL NOT render a disabled View transcript action
