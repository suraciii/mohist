## ADDED Requirements

### Requirement: Issue detail page supports issue workflow profile YAML editing
The Issue Detail page SHALL expose an issue-scoped workflow profile YAML editor with view, edit, dirty-state, save-progress, and validation-error feedback for the issue workflow profile snapshot.

#### Scenario: Backlog issue can edit workflow YAML
- **WHEN** a user opens an issue detail page for an issue whose workflow has not started
- **THEN** the page shows the issue-scoped workflow profile YAML
- **AND** the user can edit the YAML before starting the workflow

#### Scenario: Dirty state is visible
- **WHEN** the user changes the YAML content without saving
- **THEN** the page shows that the editor has unsaved changes

#### Scenario: Save progress is visible
- **WHEN** the user saves edited workflow YAML
- **THEN** the page shows a saving state until the request completes
- **AND** the editor does not report save success before the server response returns

#### Scenario: Validation errors are shown inline
- **WHEN** the save request returns YAML parsing or workflow validation errors
- **THEN** the page shows those validation errors without discarding the user's unsaved editor content

#### Scenario: Save response refreshes editor safely
- **WHEN** the save request succeeds
- **THEN** the page updates the editor with the normalized YAML returned by the server
- **AND** the page clears the dirty state against that normalized YAML snapshot
