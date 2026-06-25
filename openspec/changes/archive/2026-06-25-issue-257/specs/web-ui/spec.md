## ADDED Requirements

### Requirement: Web UI workflow profile selection is consistent

The Web UI SHALL read and write the issue's workflow profile selection as a single fact across the issue create form, the issue detail page, and the workflow-profile page. The value displayed on every surface SHALL be the issue's effective `workflowProfileId` resolved from the single source of truth. The create form selector, the issue detail display, and the workflow-profile page SHALL never present divergent profile values for the same issue.

#### Scenario: Create form selects workflow profile

- **WHEN** a user selects `mohist/pr` in the issue create form's workflow profile selector
- **THEN** the create request SHALL include `workflowProfileId: "mohist/pr"`
- **AND** the created issue's detail page SHALL display workflow profile `mohist/pr`

#### Scenario: Issue detail displays effective profile

- **WHEN** a user views an issue whose effective profile is `mohist/pr`
- **THEN** the issue detail page SHALL display `mohist/pr`
- **AND** the workflow-profile sub-page SHALL display the same `mohist/pr`

#### Scenario: Change profile from issue detail

- **WHEN** a user changes a backlog issue's workflow profile from `mohist/default` to `mohist/pr` on the issue detail page
- **THEN** the issue detail, workflow-profile page, and issue list SHALL all reflect `mohist/pr`

#### Scenario: Started issue blocks profile change in Web UI

- **WHEN** a user attempts to change the workflow profile of an issue that has an active workflow run
- **THEN** the Web UI SHALL surface a clear error that the issue has started
- **AND** the issue's workflow profile selection SHALL remain unchanged

#### Scenario: Create without selection inherits default

- **WHEN** a user creates an issue without choosing a workflow profile in the create form
- **THEN** the created issue SHALL have no issue-level selection
- **AND** the issue detail SHALL display the inherited default profile
