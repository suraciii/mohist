## ADDED Requirements

### Requirement: Epic Web Navigation and Creation

Web UI SHALL provide a separate Epic work surface outside the issue workflow Board.

#### Scenario: Epics navigation entry

- **WHEN** a user views the main navigation
- **THEN** the navigation includes `Epics`
- **AND** Epics are not shown in Board lanes

#### Scenario: Create Epic in Web UI

- **WHEN** a user opens the create Epic form
- **THEN** the form asks for title, description, and priority
- **AND** it does not require structured success criteria or decision history

### Requirement: Epic List Web UI

Web UI SHALL list Epics with enough information for users to understand progress and next action quickly.

#### Scenario: List Epics with progress

- **WHEN** a user opens the Epics page
- **THEN** each Epic shows status, title, priority, delivered/total progress, and backend-provided next issue or ready-to-mark-done state

#### Scenario: Distinguish lifecycle groups

- **WHEN** Epics have active, done, or closed statuses
- **THEN** the list groups or clearly distinguishes those statuses

### Requirement: Epic Detail Web UI

Web UI SHALL show an Epic detail page centered on goal, progress, next issue, and linked issues.

#### Scenario: View Epic detail

- **WHEN** a user opens an Epic detail page
- **THEN** the page shows description, status, priority, delivered/total progress, next issue, and linked issues with current issue states

#### Scenario: Add linked issue

- **WHEN** a user adds an existing issue from Epic detail
- **THEN** the issue is linked to the Epic
- **AND** duplicate membership errors are shown clearly

#### Scenario: Remove linked issue

- **WHEN** a user removes a linked issue from Epic detail
- **THEN** the issue disappears from that Epic's linked issue list

#### Scenario: Lifecycle actions

- **WHEN** a user marks an Epic done or closes it from the detail page
- **THEN** the page updates to show the new Epic status

### Requirement: Issue Detail Epic Backlink

Web UI SHALL display a compact backlink from Issue Detail to the issue's primary Epic when membership exists.

#### Scenario: Linked issue shows Epic backlink

- **WHEN** a user opens Issue Detail for an issue linked to an Epic
- **THEN** the page shows `Part of Epic` with Epic id and title
- **AND** clicking the link opens the Epic detail page

#### Scenario: Unlinked issue hides Epic backlink

- **WHEN** a user opens Issue Detail for an issue without Epic membership
- **THEN** no empty or misleading Epic backlink is displayed
