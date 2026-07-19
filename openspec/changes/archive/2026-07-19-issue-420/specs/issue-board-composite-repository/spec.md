### Requirement: Issue cards identify their target repository

Every issue card SHALL display the issue's target repository name as a repository label. The label SHALL represent the issue's persisted repository assignment, including the resolved default assignment, rather than the project's current default repository.

#### Scenario: Card shows an explicit repository assignment

- **WHEN** the board renders an issue assigned to repository `web`
- **THEN** the issue card SHALL display `web` as its target repository

#### Scenario: Card shows a resolved default assignment

- **WHEN** the board renders an issue that was assigned to the default repository at creation time
- **THEN** the issue card SHALL display that persisted repository name
- **AND** changing the project's default repository SHALL NOT change the repository shown for the existing issue

### Requirement: Parent cards summarize composite progress

An issue card whose issue has one or more children SHALL identify the issue as a parent and SHALL display progress in the form `X/Y done`, where `X` is the number of children with status `done` and `Y` is the total number of children. Cancelled children SHALL remain part of `Y` and SHALL NOT contribute to `X`. A card without children SHALL NOT display composite progress.

#### Scenario: Parent card shows completed-child progress

- **WHEN** a parent issue has four children, two with status `done`, one `in_progress`, and one `cancelled`
- **THEN** its board card SHALL display `2/4 done`

#### Scenario: Ordinary issue has no composite progress

- **WHEN** an issue has no children
- **THEN** its board card SHALL NOT display a parent progress indicator

### Requirement: Parent cards surface blocked children

A parent issue card SHALL display an attention indicator when at least one current child has blocked health. The indicator SHALL remain present independently of the parent's own health and SHALL make the number of blocked children available to the user. It SHALL disappear when no current child is blocked.

#### Scenario: One or more children are blocked

- **WHEN** a parent issue has two children with blocked health
- **THEN** its board card SHALL display a child-blocked attention indicator
- **AND** the indicator SHALL communicate that two children are blocked

#### Scenario: No child is blocked

- **WHEN** none of a parent issue's current children has blocked health
- **THEN** its board card SHALL NOT display a child-blocked attention indicator

### Requirement: The board filters by target repository

The board SHALL provide a target-repository filter on desktop and mobile. Selecting a repository SHALL retain only issues whose persisted target repository name matches the selection, SHALL apply across every status column, and SHALL compose with existing priority, label, title-search, sorting, cancelled-visibility, and archive behaviors. Clearing the repository filter SHALL restore issues subject to the remaining board state.

#### Scenario: Repository selection filters every column

- **WHEN** the user selects repository `server`
- **THEN** every visible board column SHALL contain only issues assigned to `server`

#### Scenario: Repository filter composes with another filter

- **WHEN** the user selects repository `web` and priority `p1`
- **THEN** the board SHALL display only issues assigned to `web` whose priority is `p1`

#### Scenario: Clearing the repository filter preserves other filters

- **WHEN** the user clears an active repository filter while a label filter remains active
- **THEN** issues from all repositories SHALL become eligible for display
- **AND** the label filter SHALL remain applied

### Requirement: Repository filter state is shareable and resilient

The selected repository SHALL be represented in the board URL query state and restored when that URL is loaded or navigated through browser history. An absent or unknown repository value SHALL NOT prevent the board from rendering; an unknown value SHALL produce an empty filtered result until it is cleared or replaced.

#### Scenario: Repository filter survives reload

- **WHEN** a user loads a board URL containing the repository filter for `web`
- **THEN** the filter control SHALL show `web` as selected
- **AND** the board SHALL display only issues assigned to `web`

#### Scenario: Unknown repository query value

- **WHEN** the board URL contains a repository name that no issue in the current project uses
- **THEN** the board SHALL render successfully with no matching issue cards
- **AND** the user SHALL be able to clear or replace the filter

### Requirement: Single-repository boards remain low-friction

When the current project declares exactly one repository, cards SHALL still expose that repository assignment, but the board SHALL NOT require the user to choose a repository to see all issues.

#### Scenario: Project has one repository

- **WHEN** the board opens for a project with exactly one declared repository and no repository query filter
- **THEN** all issues permitted by the remaining board state SHALL be shown
- **AND** no repository selection SHALL be required
