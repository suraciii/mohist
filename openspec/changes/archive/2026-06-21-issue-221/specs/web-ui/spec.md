## ADDED Requirements

### Requirement: Web UI provides a project-scoped label catalog management page

The Web UI SHALL provide a project-scoped surface, reachable from the project context (e.g. Project Settings or the project detail view), where users view and curate the project's label catalog governed by the `label-catalog` capability. The page SHALL list every catalog entry showing its `key`, `description`, `supportedValues` (when present), and `origin` (`system` or `user`). The page SHALL let users add new user-origin definitions (`key`, `description`, optional `supportedValues`), edit an existing entry's `description` and `supportedValues`, and delete user-origin entries; the `key` SHALL be immutable on edit. System-origin entries SHALL be read-only on this surface — neither editable nor deletable (both the edit and delete actions SHALL be hidden or disabled for them), consistent with the `label-catalog` capability's read-only contract for system definitions. The page SHALL consume the existing catalog API (`GET/POST/PATCH/DELETE /api/projects/{projectRef}/labels/catalog`) and SHALL NOT alter any Issue's labels. Client-side validation SHALL enforce the `label-catalog` rules: a `key` SHALL match `^[a-z0-9]([-a-z0-9]*[a-z0-9])?$`, a `description` SHALL be a non-empty, non-whitespace string, and each `supportedValues` entry SHALL be non-empty; invalid input SHALL be rejected with a clear error before any request is sent. API errors (unknown key, validation failure, conflict, or system-definition protection) SHALL be surfaced clearly in the page.

#### Scenario: Page lists all catalog entries
- **WHEN** a user opens the label catalog management page for a project that has a system `refactor` definition and a user `module` definition
- **THEN** the page lists both entries
- **AND** each row shows the key, description, supportedValues (when present), and origin

#### Scenario: Add a user definition
- **WHEN** a user enters key `module`, description "Classifies the subsystem", and supportedValues `auth,ui` and submits
- **THEN** the page sends `POST /api/projects/{projectRef}/labels/catalog` with those values
- **AND** the new entry appears in the list with `origin: user`

#### Scenario: Edit an existing entry's description and supported values
- **WHEN** a user edits the `module` entry's description and supportedValues and saves
- **THEN** the page sends `PATCH /api/projects/{projectRef}/labels/catalog/module` with the changed fields
- **AND** the entry's `key` is not editable in the form

#### Scenario: Delete a user entry
- **WHEN** a user deletes the user-origin `module` entry
- **THEN** the page sends `DELETE /api/projects/{projectRef}/labels/catalog/module`
- **AND** the entry is removed from the list

#### Scenario: System entries are read-only
- **WHEN** a user views the system-origin `refactor` entry
- **THEN** both the edit and delete actions are hidden or disabled
- **AND** no request is sent that would modify or remove a system definition

#### Scenario: Invalid input is rejected before submit
- **WHEN** a user enters an uppercase key `Module`, a leading-dash key `-mod`, or a whitespace-only description and submits
- **THEN** the page shows a clear validation error
- **AND** no API request is sent

#### Scenario: API errors are surfaced
- **WHEN** an add, edit, or delete request fails with a 400, 404, or 409
- **THEN** the page displays the server-provided error message
- **AND** the list is not left in an inconsistent state

#### Scenario: Catalog management does not touch issue labels
- **WHEN** a user adds, edits, or removes a catalog entry from this page
- **THEN** no Issue's labels are modified as a side effect
