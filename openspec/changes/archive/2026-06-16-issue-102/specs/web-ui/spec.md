## ADDED Requirements

### Requirement: Create-issue dialog displays recommended workflow from frontmatter

The Web UI create-issue dialog SHALL detect and display workflow recommendation metadata when the issue body text contains valid YAML frontmatter with `recommended_workflow` and `recommended_workflow_reason`.

#### Scenario: Dialog shows recommendation when frontmatter present

- **WHEN** a user opens the create-issue dialog
- **AND** the body text contains frontmatter with `recommended_workflow: feature-flow` and `recommended_workflow_reason: "UI changes and new feature work match feature-flow criteria"`
- **THEN** the dialog SHALL display the recommended workflow name alongside the reason
- **AND** the workflow selector SHALL be pre-filled with the recommended value

#### Scenario: Dialog does not show recommendation when no frontmatter

- **WHEN** a user opens the create-issue dialog
- **AND** the body text does not contain YAML frontmatter
- **THEN** the dialog SHALL NOT display a recommendation section
- **AND** the workflow selector SHALL use the default behavior

#### Scenario: Malformed frontmatter is handled gracefully in dialog

- **WHEN** the body text contains malformed YAML frontmatter
- **THEN** the dialog SHALL ignore the malformed metadata
- **AND** the dialog SHALL fall back to default workflow selection behavior

### Requirement: Create-issue dialog supports one-click acceptance of recommended workflow

When a workflow recommendation is present, the create-issue dialog SHALL provide a one-click action to accept the recommendation and proceed.

#### Scenario: One-click accept applies recommendation

- **WHEN** the create-issue dialog shows a workflow recommendation
- **AND** the user clicks the accept action or submits the form without changing the workflow
- **THEN** the issue SHALL be created with the recommended `workflowProfileId`
- **AND** the `risk` value from the frontmatter SHALL be applied

#### Scenario: User overrides recommendation before create

- **WHEN** the create-issue dialog shows a workflow recommendation
- **AND** the user manually changes the workflow selector to a different profile
- **THEN** the issue SHALL be created with the user-selected workflow profile
- **AND** the frontmatter recommendation SHALL be ignored

### Requirement: Create-issue dialog parses risk from body frontmatter

The Web UI create-issue dialog SHALL parse the `risk` field from body frontmatter and pre-fill the risk selector when present.

#### Scenario: Risk pre-filled from frontmatter

- **WHEN** a user opens the create-issue dialog
- **AND** the body text contains frontmatter with `risk: high`
- **THEN** the dialog SHALL pre-select `high` in the risk control
