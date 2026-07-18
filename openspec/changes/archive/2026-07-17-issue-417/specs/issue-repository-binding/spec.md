### Requirement: Every Issue has one stable target repository

Every Issue SHALL store exactly one target repository as the canonical Project-local resource name. Creation with an explicit target SHALL resolve that name case-insensitively against repositories declared by the same Project and persist the declaration's canonical name. Creation without an explicit target SHALL persist the Project's current default repository. Changing repository metadata or selecting a different Project default MUST NOT rewrite an existing Issue binding.

#### Scenario: Create with an explicit target

- **WHEN** Project `product` declares repository `web` and an Issue is created with target `WEB`
- **THEN** the Issue SHALL be created with canonical target repository `web`

#### Scenario: Create on the current default

- **WHEN** a Project whose default repository is `server` creates an Issue without an explicit target
- **THEN** the Issue SHALL persist `server` as its target without requiring a repository selection from the caller

#### Scenario: Reject an unknown target

- **WHEN** Issue creation specifies a repository name that the Project does not declare
- **THEN** creation SHALL fail with an error identifying the unknown repository and no Issue SHALL be created

#### Scenario: A default change does not retarget an Issue

- **WHEN** an Issue is bound to `server` and the Project later makes `web` the default repository
- **THEN** the existing Issue SHALL remain bound to `server` while later Issues created without a target SHALL bind to `web`

### Requirement: Target repository reassignment ends when workflow execution starts

An Issue that has never started workflow execution SHALL allow its target repository to be changed to another repository declared by the same Project. Reassignment SHALL validate and canonicalize the new name before changing the Issue. Mohist SHALL treat workflow execution as started when the Issue first durably records a workflow run and enters `in_progress`; that transition SHALL permanently lock the target repository. A failure before that transition commits MUST NOT lock the binding, while a later workflow or workspace failure MUST NOT unlock it.

#### Scenario: Reassign an unstarted Issue

- **WHEN** an Issue that has never started is changed from repository `server` to declared repository `web`
- **THEN** the update SHALL succeed and subsequent reads and execution SHALL use target `web`

#### Scenario: Reject an unknown reassignment

- **WHEN** an unstarted Issue is reassigned to a repository that its Project does not declare
- **THEN** the update SHALL fail and the Issue SHALL retain its previous target repository

#### Scenario: Reject reassignment after workflow start

- **WHEN** an Issue has started workflow execution and a client attempts to change its target repository
- **THEN** the update SHALL fail with a conflict and the original target SHALL remain unchanged

#### Scenario: Terminal status does not unlock a started Issue

- **WHEN** an Issue that previously started reaches `done` or `cancelled`
- **THEN** its target repository SHALL remain locked in the terminal status

#### Scenario: Reopening a cancelled Issue does not unlock it

- **WHEN** an Issue that previously started is cancelled and later reopened to `backlog`
- **THEN** its target repository SHALL remain locked after reopening

#### Scenario: A rejected start does not lock the target

- **WHEN** an Issue start fails before the Issue records a workflow run and enters `in_progress`
- **THEN** the Issue SHALL remain eligible for target repository reassignment

#### Scenario: Workspace failure after recorded start keeps the target locked

- **WHEN** an Issue records its workflow run and enters `in_progress` but later workspace preparation fails
- **THEN** the Issue's target repository SHALL remain locked for retry or recovery

#### Scenario: Start and reassignment are serialized

- **WHEN** workflow start races reassignment of the same unstarted Issue to `web`
- **THEN** either reassignment SHALL commit first and the run SHALL use `web`, or start SHALL commit first and reassignment SHALL fail without changing the run target

### Requirement: Issue reads expose the stored target repository

Issue list and detail results SHALL expose the Issue's stored canonical target repository name. When the declaration exists, repository metadata SHALL be resolved from that named declaration. When a terminal Issue retains a target whose declaration has been removed, reads SHALL continue to expose the stored target name as unresolved and MUST NOT substitute the current default repository. Human-readable Issue detail output, including `mo issue show`, SHALL identify the target repository.

#### Scenario: Read a declared target

- **WHEN** an Issue is bound to declared repository `web`
- **THEN** list and detail results SHALL identify `web` as the target and resolve metadata from the `web` declaration

#### Scenario: Read a historical unresolved target

- **WHEN** a terminal Issue remains bound to `web` after the `web` declaration is removed
- **THEN** Issue detail SHALL identify `web` as the unresolved stored target and MUST NOT display the Project default as its target

#### Scenario: Show identifies the target

- **WHEN** a user runs `mo issue show <number>` in human-readable output mode
- **THEN** the output SHALL include the Issue's target repository name

### Requirement: Issue lists filter by stored target repository

Issue listing SHALL accept a target-repository filter and return only Issues whose stored target name matches the filter case-insensitively. The repository filter SHALL compose with existing Issue filters and SHALL use the stored binding rather than the current default flag or Git metadata.

#### Scenario: Filter a multi-repository Project

- **WHEN** a Project has Issues bound to `server` and `web` and the list is filtered by repository `SERVER`
- **THEN** every returned Issue SHALL be bound to `server` and no Issue bound to `web` SHALL be returned

#### Scenario: Compose repository and status filters

- **WHEN** the Issue list is filtered by repository `server` and status `in_progress`
- **THEN** every returned Issue SHALL satisfy both filters

#### Scenario: Filter by a removed historical target

- **WHEN** terminal Issues retain target `web` after its declaration is removed and the list is filtered by repository `web`
- **THEN** those Issues SHALL remain discoverable when the requested list includes their terminal status

### Requirement: The Issue CLI uses `--repo`

The Issue CLI SHALL use `--repo <name>` for repository-aware `mo issue create`, `mo issue update`, and `mo issue list` operations. Creation SHALL select an explicit target or omit the option to use the default, update SHALL request an eligible reassignment, and list SHALL filter by the stored target. The replaced `--repository` option MUST NOT remain accepted by Issue commands.

#### Scenario: Create through the CLI

- **WHEN** a user runs `mo issue create "Web change" --repo web`
- **THEN** the created Issue SHALL be bound to repository `web`

#### Scenario: Update through the CLI

- **WHEN** a user runs `mo issue update 42 --repo web` for an Issue that has never started
- **THEN** the CLI SHALL request reassignment and report `web` as the resulting target on success

#### Scenario: Filter through the CLI

- **WHEN** a user runs `mo issue list --repo server`
- **THEN** the CLI SHALL display only Issues bound to repository `server`

#### Scenario: Reject the replaced option

- **WHEN** a user passes `--repository` to an Issue command
- **THEN** command parsing SHALL fail without sending an Issue request
