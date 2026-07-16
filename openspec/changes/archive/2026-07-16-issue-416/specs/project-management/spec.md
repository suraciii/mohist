### Requirement: Projects own repository resources

Every Project available for use SHALL own one or more repository resources. Each repository MUST have a non-empty project-local resource name, Git URL, and base branch, and MUST expose whether it is the Project default. Repository names SHALL be unique within a Project under case-insensitive comparison, repository declarations SHALL NOT be shared across Projects, and every successfully committed and returned Project state SHALL have exactly one default repository.

#### Scenario: A Project exposes a complete repository declaration
- **WHEN** a Project is created or read
- **THEN** it SHALL contain at least one repository
- **AND** every repository SHALL contain a non-empty `name`, `gitUrl`, and `baseBranch`
- **AND** exactly one repository SHALL have `isDefault` set to `true`

#### Scenario: Repository names are scoped to one Project
- **WHEN** two Projects each declare a repository named `server`
- **THEN** both declarations SHALL be accepted
- **AND** changing either declaration SHALL NOT change the other Project

### Requirement: Repository addition preserves identity and the default invariant

Adding a repository SHALL require a non-empty resource name and Git URL. If the base branch is omitted or blank, the repository SHALL use `main`. Adding without default selection SHALL preserve the existing default, while adding with default selection SHALL atomically make the new repository the sole default. An attempted duplicate name, including a name that differs only by letter case, MUST be rejected without changing the Project.

#### Scenario: Add a second non-default repository
- **WHEN** a Project whose `server` repository is default adds `web` without requesting default selection
- **THEN** the Project SHALL contain both repositories
- **AND** `server` SHALL remain the sole default
- **AND** `web` SHALL be non-default

#### Scenario: Add and select a new default repository
- **WHEN** a Project whose `server` repository is default adds `web` with default selection
- **THEN** `web` SHALL become the sole default
- **AND** `server` SHALL become non-default

#### Scenario: Reject a duplicate repository name
- **WHEN** a Project already contains a repository named `server`
- **AND** a client attempts to add a repository named `SERVER`
- **THEN** the system SHALL reject the addition as a conflict
- **AND** the existing repositories and default selection SHALL remain unchanged

#### Scenario: Reject incomplete repository metadata
- **WHEN** a client attempts to add a repository with a blank name or Git URL
- **THEN** the system SHALL reject the addition with a validation error
- **AND** the Project SHALL remain unchanged

### Requirement: Repository updates preserve stable references

A repository resource name SHALL be its stable reference and MUST NOT be changed by an update. Updating a repository SHALL allow its Git URL and base branch to change while preserving its name and default status. An update MUST identify an existing repository and MUST supply at least one supported metadata field; otherwise it SHALL be rejected without mutation.

#### Scenario: Update repository metadata
- **WHEN** a client updates the Git URL and base branch of repository `web`
- **THEN** the system SHALL persist and return the new Git URL and base branch
- **AND** the repository SHALL remain named `web`
- **AND** its default status SHALL remain unchanged

#### Scenario: Reject repository renaming
- **WHEN** a client attempts to change repository `web` to a different resource name
- **THEN** the system SHALL reject the update
- **AND** the existing repository declaration SHALL remain unchanged

#### Scenario: Reject an empty repository update
- **WHEN** a client requests an update without supplying a Git URL or base branch
- **THEN** the system SHALL reject the request with an actionable validation error
- **AND** the repository declaration SHALL remain unchanged

### Requirement: Default selection and deletion preserve a usable Project

Selecting an existing repository as default SHALL atomically make it the sole default and SHALL be idempotent when it is already default. A non-default repository SHALL be deletable. The default repository MUST NOT be deleted; the system SHALL reject that operation and instruct the caller to select another default first. A mutation naming a repository that does not exist SHALL fail without changing the Project.

#### Scenario: Switch the default repository
- **WHEN** a Project whose `server` repository is default selects `web` as default
- **THEN** `web` SHALL become the sole default
- **AND** `server` SHALL remain declared as a non-default repository

#### Scenario: Select the current default again
- **WHEN** a client selects the repository that is already default
- **THEN** the operation SHALL succeed without changing repository metadata
- **AND** that repository SHALL remain the sole default

#### Scenario: Delete a non-default repository
- **WHEN** a Project contains default repository `server` and non-default repository `web`
- **AND** a client deletes `web`
- **THEN** `web` SHALL be removed
- **AND** `server` SHALL remain the sole default

#### Scenario: Reject deletion of the default repository
- **WHEN** a client attempts to delete the default repository `server`
- **THEN** the system SHALL reject the operation as a conflict
- **AND** the error SHALL instruct the caller to select a different default repository first
- **AND** no repository or default status SHALL change

#### Scenario: Reject mutation of an unknown repository
- **WHEN** a client attempts to update, select, or delete a repository name that the Project does not contain
- **THEN** the system SHALL return a not-found result identifying that repository
- **AND** the Project SHALL remain unchanged

### Requirement: Project creation is repository-backed and atomic

A successful Project creation SHALL atomically create the Project with one initial repository and mark that repository as default. The creation input MUST provide or resolve a non-empty resource name, Git URL, and base branch for the initial repository. The Project model SHALL store repository metadata but SHALL NOT store a project-level local path, checkout path, Git URL, or base branch. A Project MUST NOT become observable if its initial repository cannot be created.

#### Scenario: Create a Project with its initial repository
- **WHEN** a client creates a Project with valid initial repository metadata
- **THEN** the system SHALL create the Project and repository in one operation
- **AND** the returned Project SHALL contain only that repository
- **AND** that repository SHALL be the default

#### Scenario: Reject repository-less Project creation
- **WHEN** a client requests Project creation without initial repository metadata
- **THEN** the system SHALL reject the request with a validation error
- **AND** no Project SHALL be created

#### Scenario: Initial repository creation fails
- **WHEN** Project creation supplies invalid initial repository metadata
- **THEN** the system SHALL reject the request
- **AND** neither a Project nor a partial repository declaration SHALL be persisted

### Requirement: Existing Projects upgrade without repository metadata loss

The data upgrade SHALL bring every existing Project repository declaration under the exactly-one-default invariant. It MUST preserve each repository's resource name, Git URL, base branch, and declaration order. If a Project already has exactly one marked default, that selection SHALL be preserved. If no repository is marked default, the first repository in the existing declaration order SHALL become default. If more than one repository is marked default, the first marked repository in the existing declaration order SHALL remain default and the others SHALL become non-default. Existing Project and issue identities MUST remain unchanged. The upgrade MUST NOT fabricate repository metadata; a Project with no recoverable repository declaration or with declarations that cannot satisfy the required metadata and name-uniqueness rules SHALL stop the upgrade with an actionable diagnostic, and its persisted data SHALL remain unchanged.

#### Scenario: Upgrade a single-repository Project
- **WHEN** an existing Project has one repository declaration that is not marked default
- **THEN** the upgrade SHALL preserve its name, Git URL, and base branch
- **AND** SHALL mark that repository as the Project default
- **AND** SHALL preserve the Project identity

#### Scenario: Preserve an existing valid default
- **WHEN** an existing Project already has one default among its repository declarations
- **THEN** the upgrade SHALL preserve that default selection
- **AND** SHALL preserve all repository names, Git URLs, and base branches

#### Scenario: Normalize a missing default deterministically
- **WHEN** an existing Project has multiple repository declarations and none is marked default
- **THEN** the upgrade SHALL mark the first declared repository as default
- **AND** SHALL preserve the declaration order and every repository's name, Git URL, and base branch

#### Scenario: Normalize multiple defaults deterministically
- **WHEN** an existing Project has multiple repository declarations marked default
- **THEN** the upgrade SHALL preserve the first marked repository as default
- **AND** SHALL mark every other repository as non-default
- **AND** SHALL preserve the declaration order and every repository's name, Git URL, and base branch

#### Scenario: Reject unrecoverable repository-less legacy data
- **WHEN** an existing Project has no repository declaration or other repository metadata from which one can be recovered
- **THEN** the upgrade SHALL fail with an actionable diagnostic identifying the Project
- **AND** SHALL NOT invent a resource name, Git URL, or base branch
- **AND** SHALL leave the Project and its issues unchanged

#### Scenario: Reject conflicting legacy repository names
- **WHEN** an existing Project has repository declarations whose names differ only by letter case
- **THEN** the upgrade SHALL fail with an actionable diagnostic identifying the Project and conflicting names
- **AND** SHALL NOT rename or discard either repository
- **AND** SHALL leave the Project and its issues unchanged

### Requirement: Existing issue execution remains continuous

The upgrade SHALL preserve existing Project and Issue identities. An existing issue whose Project previously had a single repository SHALL continue to start and execute using that repository after it becomes the default. This change SHALL NOT create or change an Issue target-repository binding or add one-issue multi-repository execution.

#### Scenario: Start an existing issue after upgrade
- **WHEN** an existing unstarted issue has no repository selection and its Project has been upgraded
- **THEN** issue startup SHALL succeed using the upgraded default repository's Git URL and base branch
- **AND** the issue identity and existing issue data SHALL remain unchanged

#### Scenario: Preserve an in-flight issue across upgrade
- **WHEN** an issue workflow is already running against the Project's existing repository during the upgrade
- **THEN** the workflow MUST remain runnable against the same repository metadata
- **AND** the upgrade SHALL NOT cancel, restart, or redirect the workflow
