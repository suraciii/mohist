## MODIFIED Requirements

### Requirement: REQ-WEB-001 Issue detail always explains merge state

The Issue detail page SHALL render a stable merge status box for every issue state, including when `mergeState` is null.

#### Scenario: Null merge state before ready
- **GIVEN** an issue has `mergeState=null`
- **AND** the issue is not done/completed
- **WHEN** the user opens the Issue detail page
- **THEN** the page SHALL explain whether the issue is not ready, not merged, or waiting for approval/merge intent based on stage and status

#### Scenario: False-done warning
- **GIVEN** an issue has `stage=done`
- **AND** `status=completed`
- **AND** `mergeState` is null or not `merged`
- **WHEN** the user opens the Issue detail page
- **THEN** the page SHALL show a prominent Done but not merged warning

### Requirement: REQ-WEB-002 Issue cards expose merge trust

Issue cards SHALL display merge success or merge warning badges so the user can identify merged, queued, conflicted, unknown, and false-done issues without opening the detail page.

#### Scenario: Done column false-done card
- **GIVEN** an issue appears in the Done column
- **AND** `mergeState` is null or not `merged`
- **WHEN** the card is rendered
- **THEN** the card SHALL show a merge warning badge or marker

### Requirement: REQ-WEB-003 Approval copy matches next action

Approval actions SHALL describe the action that will happen next rather than claiming the issue will be done before merge completes.

#### Scenario: Check approval button
- **GIVEN** an issue is awaiting Check approval
- **WHEN** the approval panel is rendered
- **THEN** the primary action SHALL NOT say `Approve & Done`
- **AND** it SHALL communicate queue merge or merge-to-target intent

#### Scenario: Plan approval button
- **GIVEN** an issue is awaiting Plan approval
- **WHEN** the approval panel is rendered
- **THEN** the primary action SHALL communicate approving the design and starting or resuming build
