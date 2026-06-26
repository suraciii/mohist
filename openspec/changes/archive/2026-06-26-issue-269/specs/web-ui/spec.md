## ADDED Requirements

### Requirement: Settings Workflows exposes project default workflow configuration

The Web UI Settings → Workflows page SHALL surface a project default workflow control that answers "what workflow will new issues inherit for the active project?". The control SHALL read the active project's current default template from `GET /api/projects/{projectRef}/workflow-profile`. The user SHALL be able to set the project default by writing `PUT /api/projects/{projectRef}/workflow-profile/default-template`, and SHALL be able to clear the project default by calling `DELETE /api/projects/{projectRef}/workflow-profile/default-template`. After any write or delete, the control SHALL read back and display the resolved `defaultTemplateId`.

#### Scenario: Display current project default workflow

- **WHEN** a user opens Settings → Workflows for a project whose `defaultTemplateId` is `mohist/github-pr`
- **THEN** the project default workflow control SHALL display `mohist/github-pr` as the project's current default
- **AND** the control SHALL read its value from `GET /api/projects/{projectRef}/workflow-profile`

#### Scenario: Select a project default workflow

- **WHEN** a user selects `mohist/github-pr` in the project default workflow control
- **THEN** the UI SHALL send `PUT /api/projects/{projectRef}/workflow-profile/default-template` with `templateId: "mohist/github-pr"`
- **AND** the readback SHALL confirm `defaultTemplateId: "mohist/github-pr"`

#### Scenario: Clear the project default workflow

- **WHEN** a user clears the project default workflow selection
- **THEN** the UI SHALL send `DELETE /api/projects/{projectRef}/workflow-profile/default-template`
- **AND** the control SHALL explain that the project is inheriting the system default

#### Scenario: Project default control reflects unset state

- **WHEN** a user opens Settings → Workflows for a project whose `defaultTemplateId` is unset
- **THEN** the control SHALL indicate that no project default is configured
- **AND** the control SHALL communicate that the project inherits the system default

### Requirement: System default metadata is visually distinguished from project default state

The Web UI SHALL visually distinguish the static system-default metadata (the `isDefault` flag on built-in profiles such as `mohist/default`) from the active project's current default workflow state. The system-default badge SHALL NOT be presented in a way that could be mistaken for the project default. The project default state SHALL be derived from the project workflow-profile read model, not from the static `isDefault` catalog flag.

#### Scenario: System default badge is not mistaken for project default

- **WHEN** a user views the workflow catalog where `mohist/default` carries the static `isDefault` flag and the project's `defaultTemplateId` is unset
- **THEN** the system-default badge SHALL NOT imply that `mohist/default` is this project's configured default
- **AND** the project default control SHALL separately indicate that the project is inheriting the system default

#### Scenario: Project default state sourced from project read model

- **WHEN** the Web UI renders which workflow is the project's current default
- **THEN** the project default state SHALL be sourced from the project workflow-profile read model
- **AND** the project default state SHALL NOT be derived solely from `workflowProfiles.find((p) => p.isDefault)`

### Requirement: Profile selection surfaces resolve default from project configuration

Create-issue and profile-selection surfaces SHALL resolve the default workflow profile from the project's configured default template when present, falling back to the system default only when the project default is unset. These surfaces SHALL NOT resolve the default solely from a hardcoded `isDefault` lookup on the workflow profile catalog.

#### Scenario: Create issue inherits project default when configured

- **WHEN** a user opens the create-issue dialog for a project whose `defaultTemplateId` is `mohist/github-pr` and does not choose a workflow profile
- **THEN** the default workflow profile selection SHALL resolve to `mohist/github-pr`
- **AND** the create request SHALL carry the project-configured default rather than a value derived only from `isDefault`

#### Scenario: Create issue falls back to system default when project default unset

- **WHEN** a user opens the create-issue dialog for a project whose `defaultTemplateId` is unset and does not choose a workflow profile
- **THEN** the default workflow profile selection SHALL fall back to the system default profile
- **AND** the selection SHALL NOT require a project default to be configured
