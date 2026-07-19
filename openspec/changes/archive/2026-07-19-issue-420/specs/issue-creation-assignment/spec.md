### Requirement: New Issue selects a target repository

The New Issue form SHALL allow the user to select one repository declared by the current project and SHALL submit that repository as the issue's target assignment. In a multi-repository project, the project's current default repository SHALL be selected initially. In a single-repository project, that sole repository SHALL be used without requiring an explicit user decision.

#### Scenario: User selects a repository in a multi-repository project

- **WHEN** the current project declares `server` as default and `web` as another repository, and the user selects `web`
- **THEN** the create request SHALL assign the new issue to `web`
- **AND** the created issue SHALL report `web` as its target repository

#### Scenario: User keeps the multi-repository default

- **WHEN** the New Issue form opens for a multi-repository project whose default repository is `server`
- **THEN** `server` SHALL be selected initially
- **AND** creating without changing that selection SHALL assign the issue to `server`

#### Scenario: Project has one repository

- **WHEN** the current project declares only repository `main`
- **THEN** creating an issue SHALL assign it to `main`
- **AND** the user SHALL NOT be required to choose a repository

### Requirement: New Issue selects an eligible parent

The New Issue form SHALL provide an optional parent-issue selection scoped to the current project. Candidate options SHALL expose issue number and title and SHALL exclude issues that are not eligible to be parents under the current issue relationship rules, including terminal issues, child issues, and issues otherwise unavailable as a parent. Leaving the selection empty SHALL create an ordinary issue without a parent.

#### Scenario: User creates a child issue

- **WHEN** the user selects eligible parent `#42` and submits a valid New Issue form
- **THEN** the create request SHALL assign `#42` as the new issue's parent
- **AND** the created issue SHALL expose a parent reference to `#42`

#### Scenario: User creates an ordinary issue

- **WHEN** the user leaves the parent selection empty
- **THEN** the create request SHALL NOT assign a parent

#### Scenario: Ineligible issues are not offered

- **WHEN** the current project contains a cancelled issue, an issue that is itself a child, and an eligible parent issue
- **THEN** the parent selector SHALL offer the eligible parent issue
- **AND** it SHALL NOT offer the cancelled issue or the child issue

### Requirement: Parent and repository assignments are submitted together

When both a parent and repository are selected, the New Issue form SHALL submit both assignments in one create operation. The repository assignment SHALL belong to the new child itself and SHALL be independent of the parent's repository.

#### Scenario: Child targets a different repository from its parent

- **WHEN** parent `#42` targets `server` and the user creates its child with repository `web`
- **THEN** the create request SHALL assign parent `#42` and repository `web`
- **AND** the created child SHALL target `web`

### Requirement: Assignment validation failures are actionable

The server SHALL remain authoritative for repository and parent eligibility at creation time. When creation is rejected because the repository is unknown or the selected parent is missing, terminal, itself a child, or otherwise ineligible, the New Issue form SHALL remain open, preserve the user's entered values, and display a user-understandable error that identifies the invalid assignment. The form SHALL NOT report success or clear the entered issue content.

#### Scenario: Selected repository is no longer declared

- **WHEN** a selected repository is removed before the create request is accepted
- **THEN** issue creation SHALL fail without creating the issue
- **AND** the form SHALL identify the selected repository as unavailable
- **AND** the user's other entered values SHALL remain available for correction

#### Scenario: Selected parent becomes terminal

- **WHEN** a selected parent reaches a terminal state before the create request is accepted
- **THEN** issue creation SHALL fail without creating the child
- **AND** the form SHALL explain that the selected parent is no longer eligible
- **AND** the user's other entered values SHALL remain available for correction

#### Scenario: Selected parent is itself a child

- **WHEN** the create request selects an issue that is itself a child as the parent
- **THEN** issue creation SHALL fail without creating a nested relationship
- **AND** the form SHALL explain that sub-issues cannot be used as parents

### Requirement: Successful creation refreshes relationship and board views

After a child issue is created successfully, the Web UI SHALL make the new issue available on the board under its target repository and SHALL refresh the selected parent's composite progress and child list without requiring a full-page reload.

#### Scenario: New child appears in affected views

- **WHEN** creation of a child assigned to repository `web` under parent `#42` succeeds
- **THEN** subsequent board data SHALL include the child with repository `web`
- **AND** refreshed details for `#42` SHALL include the child and updated child totals
