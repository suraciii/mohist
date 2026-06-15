## ADDED Requirements

### Requirement: WorkflowProfilesSection renders full multi-line descriptions

The Web UI `WorkflowProfilesSection` SHALL render each profile's full multi-line `description` in the profile list view, not just a one-line truncation. The description SHALL be visually distinct from the profile name and ID.

#### Scenario: Profile list shows description
- **WHEN** a user views the Workflow Profiles section in Settings
- **THEN** each profile card SHALL display the profile's display name, description, and ID
- **AND** the description SHALL render with preserved line breaks
- **AND** the description SHALL be visually more prominent than the profile ID

#### Scenario: Profile detail shows full description
- **WHEN** a user clicks into a specific profile
- **THEN** the detail view SHALL display the profile's full multi-line description at the top
- **AND** the stages summary and YAML definition SHALL appear below the description
- **AND** the description SHALL use readable formatting (not monospaced pre-formatted text unless the user explicitly views raw YAML)

#### Scenario: Profile with short description
- **WHEN** a profile has only a single-line description
- **THEN** the card and detail view SHALL still render it without truncation or formatting issues

### Requirement: WorkflowProfilesSection description is read before YAML editor

The profile detail view SHALL present the description as the primary readable metadata, with the raw YAML definition as secondary reference material that users can scroll to if needed.

#### Scenario: Description appears above YAML
- **WHEN** a user views a profile detail
- **THEN** the description SHALL appear at the top of the detail view
- **AND** the YAML editor/viewer SHALL appear in a section below
- **AND** the YAML section SHALL be clearly labeled as "Definition (YAML)" to distinguish it from the human-readable metadata

#### Scenario: First-time viewer understands the profile
- **WHEN** a user sees a profile for the first time
- **THEN** they SHALL be able to understand what the profile is for from the description alone
- **AND** they SHALL NOT need to read the raw YAML to make a selection decision
