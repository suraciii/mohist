### Requirement: Parent details show composite progress

An issue detail page for an issue with one or more current children SHALL identify it as a parent and SHALL display total child count, `X/Y done` progress, and blocked-child count. `X` SHALL count only children with status `done`; `Y` SHALL count every current child, including cancelled children; the blocked count SHALL count children with blocked health.

#### Scenario: Parent has mixed child states

- **WHEN** a parent has four children, two `done`, one blocked `in_progress`, and one `cancelled`
- **THEN** its detail page SHALL display `2/4 done`
- **AND** it SHALL display one blocked child

#### Scenario: Parent has no blocked children

- **WHEN** a parent has children and none has blocked health
- **THEN** its detail page SHALL display a blocked-child count of zero

### Requirement: Parent details list every current child

The parent issue detail page SHALL list every current child issue in deterministic issue-number order. Each child entry SHALL display the child's issue number, title, status, target repository name, and blocked state when applicable. Each entry SHALL navigate to that child issue within the current project.

#### Scenario: Parent child list contains mixed repositories and states

- **WHEN** a parent has child `#12` in repository `server` with status `done` and child `#13` in repository `web` with blocked health
- **THEN** the child list SHALL include both `#12` and `#13`
- **AND** each row SHALL show its title, status, and respective repository
- **AND** the row for `#13` SHALL identify it as blocked

#### Scenario: User opens a child from the parent

- **WHEN** the user activates a child entry on a parent detail page
- **THEN** the Web UI SHALL navigate to that child's issue detail page in the current project

### Requirement: Parent details suppress workflow-specific surfaces

An issue detail page for an issue with children SHALL NOT present workflow stage panels or workflow-run evidence as though the parent executes a workflow. This suppression SHALL include the workflow stage view, branch and diff/commit surfaces, task progress, workflow sessions, workflow artifacts, workflow profile controls, and workflow recovery or approval controls. Parent-level issue information, composite progress, child navigation, description, comments, repository metadata, prerequisites, and valid parent lifecycle actions SHALL remain available.

#### Scenario: Parent has children and no workflow run

- **WHEN** the detail page renders an issue with one or more children
- **THEN** no workflow stage panel or workflow-run evidence surface SHALL be displayed
- **AND** the composite progress and child list SHALL be displayed

#### Scenario: Parent retains non-workflow information

- **WHEN** the detail page renders a parent issue
- **THEN** its description, comments, issue status, repository assignment, and valid issue-level actions SHALL remain available

### Requirement: Child details link back to the parent

An issue detail page for a child issue SHALL display its parent issue number and title as a navigable backlink. Activating the backlink SHALL navigate to the parent issue within the current project. An issue without a parent SHALL NOT display a parent backlink.

#### Scenario: User returns from child to parent

- **WHEN** a child detail page contains a parent reference to `#42`
- **THEN** the page SHALL display `#42` and the parent title as a backlink
- **AND** activating it SHALL navigate to issue `#42` in the current project

#### Scenario: Ordinary issue has no parent backlink

- **WHEN** an issue has no parent
- **THEN** its detail page SHALL NOT display a parent-issue backlink

### Requirement: Issue details identify the target repository

Every issue detail page SHALL display the issue's persisted target repository name. The displayed assignment SHALL be available for ordinary issues, parent issues, and child issues, and SHALL NOT be inferred from the project's current default repository.

#### Scenario: Child detail shows its own repository

- **WHEN** a child issue is assigned to repository `web` and its parent is assigned to repository `server`
- **THEN** the child detail page SHALL display `web` as the child's target repository

#### Scenario: Parent detail shows its persisted repository metadata

- **WHEN** a parent issue has a persisted target repository assignment
- **THEN** the parent detail page SHALL display that repository assignment as issue metadata
- **AND** it SHALL NOT imply that the parent executes a workflow in that repository

### Requirement: Composite detail data reflects current relationships

The detail read surface SHALL provide the current parent reference, current child collection, child statuses, child health, and child repository assignments needed by the Web UI. A child that is detached SHALL no longer appear in the former parent's list or totals, and SHALL no longer display the former parent backlink.

#### Scenario: Child is detached from a parent

- **WHEN** a child relationship is removed and the parent and child details are refreshed
- **THEN** the child SHALL be absent from the former parent's child list and progress totals
- **AND** the child detail SHALL NOT display the former parent backlink

#### Scenario: Child health changes

- **WHEN** a child's health changes from blocked to active and the parent detail is refreshed
- **THEN** the parent's blocked-child count SHALL decrease accordingly
- **AND** the child row SHALL no longer identify that child as blocked
