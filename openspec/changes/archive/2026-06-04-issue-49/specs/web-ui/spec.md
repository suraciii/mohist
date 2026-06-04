## ADDED Requirements

### Requirement: Settings page has a Templates tab

The Web UI Settings page SHALL add a `Templates` tab as a top-level navigation entry, alongside the existing `Project`, `System`, and other Settings tabs. The tab SHALL be reachable at `/settings/templates` and SHALL follow the existing Settings routing and permission conventions.

#### Scenario: Settings page renders a Templates tab

- **WHEN** a user opens the Settings page
- **THEN** the tab list SHALL include a `Templates` entry alongside the other Settings tabs
- **AND** the entry SHALL have a stable testid such as `settings-tab-templates`

#### Scenario: Templates tab URL is routable

- **WHEN** a user navigates to `/settings/templates` directly (e.g. via deep link or refresh)
- **THEN** the Settings page SHALL mount the Templates section as the active tab
- **AND** it SHALL NOT redirect to another tab

#### Scenario: Templates tab requires a project context

- **WHEN** a user opens the Templates tab and no project is selected
- **THEN** the section SHALL show a "No project selected" placeholder
- **AND** it SHALL NOT issue template API requests

#### Scenario: Templates tab follows Settings tab conventions

- **WHEN** the user opens the Templates tab
- **THEN** the section SHALL render inside the same Tabs container used by other Settings sections
- **AND** it SHALL honor the existing tab-change navigation pattern (e.g. `navigate('/settings/templates')`)

### Requirement: Settings → Templates section manages prompt templates

The Settings → Templates section SHALL provide a list view, a two-pane editor, and a New Template dialog backed by the system and project template APIs. The section SHALL display each template's source (`system` / `project-override` / `project-new`), stage badge, tags, display name, and the available actions. The editor SHALL render a live preview of the body against user-editable variables and a checklist of referenced variables with their availability.

#### Scenario: Templates list shows source labels

- **WHEN** a project has a system template, an override for a system key, and a project-unique key
- **THEN** the list SHALL show three rows
- **AND** the system row's source label SHALL be `system`
- **AND** the overridden row's source label SHALL be `projectⓘ`
- **AND** the project-unique row's source label SHALL be `projectⓘ new`

#### Scenario: Templates list search filters by metadata

- **WHEN** a user types in the Templates search box
- **THEN** the list SHALL filter rows by matches in `key`, `displayName`, `tags`, or `description`
- **AND** clearing the input SHALL restore the full list

#### Scenario: Template editor opens two-pane layout

- **WHEN** a user opens a template in the editor
- **THEN** the left pane SHALL show editable fields for `displayName`, `description`, `tags`, `stage`, and `body`
- **AND** the `key` field SHALL be read-only
- **AND** the right pane SHALL show a live preview of the body against user-editable variables
- **AND** the right pane SHALL list referenced variables with `✓` / `✗` availability

#### Scenario: Template editor Save persists the override

- **WHEN** a user edits a template and clicks `Save`
- **THEN** the section SHALL send `PUT /api/projects/{id}/templates/{key}/override`
- **AND** on success it SHALL refresh the templates list and close the editor

#### Scenario: New Template dialog creates a project-unique template

- **WHEN** a user clicks `+ New Template` and submits the dialog with a `key`, `displayName`, and `body`
- **THEN** the section SHALL send `PUT /api/projects/{id}/templates/{key}/override`
- **AND** the new row SHALL appear in the list as `projectⓘ new`
