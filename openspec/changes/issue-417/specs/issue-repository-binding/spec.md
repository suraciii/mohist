### Requirement: Issues bind to one canonical target repository

Every Issue SHALL persist exactly one target repository as a canonical Project-local resource name. Whenever a target is assigned or changed, the name MUST resolve to a repository currently declared by the same Project and SHALL be stored using the declaration's canonical casing after case-insensitive matching. When creation omits a target, the Issue SHALL bind to the Project's current default repository at creation time. The binding SHALL remain the repository resource name; changing repository metadata or the Project default MUST NOT rewrite that binding. A historical binding can become unresolved only when its declaration was removed after the Issue became terminal or when pre-adoption data already references a missing declaration.

#### Scenario: Create with an explicit target repository

- **WHEN** Project `product` declares repository `web` and an Issue is created with target `WEB`
- **THEN** the Issue SHALL be created with target repository `web`
- **AND** the stored binding SHALL use the canonical declared name

#### Scenario: Create without a target repository

- **WHEN** a Project whose default repository is `server` creates an Issue without an explicit target
- **THEN** the Issue SHALL bind to `server`
- **AND** the binding SHALL be persisted as an explicit stable target

#### Scenario: Single-repository creation remains implicit

- **WHEN** a Project declares only default repository `main` and an Issue is created without a target option
- **THEN** the Issue SHALL bind to `main`
- **AND** the caller SHALL NOT be required to supply a repository selection

#### Scenario: A later default change does not retarget an Issue

- **WHEN** an Issue was bound to default repository `server` and the Project later makes `web` the default
- **THEN** the existing Issue SHALL remain bound to `server`
- **AND** Issues subsequently created without an explicit target SHALL bind to `web`

#### Scenario: Reject an unknown target during creation

- **WHEN** a client attempts to create an Issue with a target name that is not declared by the Project
- **THEN** creation SHALL be rejected with an error identifying the unknown repository
- **AND** no Issue SHALL be created

### Requirement: Existing Issues acquire a stable target binding

An existing Issue that has no persisted target repository and has never started workflow execution SHALL be bound once to its Project's current default repository without changing the Issue identity, status, or other Issue data. An existing Issue with current or historical workflow execution SHALL instead bind to the one canonical repository name consistently captured by that workflow history, even if the Project default has since changed. If a previously started Issue has no recoverable repository name or its persisted workflow contexts identify different repositories, stabilization MUST fail with an actionable diagnostic for that Issue rather than guessing a target. Stabilization SHALL be atomic per Issue: failure SHALL leave that Issue unchanged, while successful stabilization of other Issues remains valid. After a binding is established, later default changes MUST NOT retarget it. An existing non-empty repository reference that cannot be resolved MUST remain visible as the Issue's unresolved target and MUST NOT fall back to the default.

#### Scenario: Stabilize an existing unstarted Issue on the default repository

- **WHEN** an existing Issue has no persisted target, has never started, and its Project's default repository is `server`
- **THEN** the Issue SHALL acquire `server` as its persisted target
- **AND** its identity and status SHALL remain unchanged
- **AND** a later default change SHALL NOT change that target

#### Scenario: Stabilize a previously started Issue from its runtime context

- **WHEN** an existing Issue has no persisted target but its current or historical workflow runtime context names repository `server`
- **AND** the Project default is now `web`
- **THEN** the Issue SHALL acquire `server` as its persisted target
- **AND** its existing workflow association and workspace SHALL remain aligned with that target
- **AND** `web` MUST NOT be substituted

#### Scenario: Reject an unrecoverable started Issue migration

- **WHEN** an existing Issue has previously started but neither its Issue state nor persisted workflow runtime context identifies a target repository
- **THEN** adoption SHALL fail with an actionable diagnostic identifying that Issue
- **AND** the system MUST NOT assign the current default by guess
- **AND** that Issue's persisted state SHALL remain unchanged

#### Scenario: Reject conflicting historical repository contexts

- **WHEN** an existing Issue has no persisted target and its workflow history identifies more than one canonical repository name
- **THEN** stabilization SHALL fail with an actionable diagnostic identifying the conflicting names
- **AND** no target repository SHALL be written for that Issue

#### Scenario: Preserve an unresolved existing target

- **WHEN** an existing Issue records target `legacy` but the Project no longer declares `legacy`
- **THEN** the Issue SHALL continue to identify `legacy` as its target
- **AND** the read result SHALL report that the target is unresolved
- **AND** the system MUST NOT substitute the Project default

### Requirement: Target repository reassignment is allowed only before first start

An Issue that has never successfully started workflow execution SHALL allow its target repository to be changed to another repository declared by the same Project. The new name SHALL be validated and canonicalized before the update commits. The first successfully recorded workflow start SHALL permanently lock the target repository, regardless of the Issue's later status, archive state, workflow outcome, retry or rerun history, or whether its current workflow-run reference is later cleared. A start attempt rejected before execution begins SHALL NOT lock the binding.

#### Scenario: Reassign an unstarted Issue

- **WHEN** an Issue that has never started is changed from repository `server` to declared repository `web`
- **THEN** the update SHALL succeed
- **AND** subsequent reads and execution SHALL use target `web`

#### Scenario: Reject an unknown reassignment atomically

- **WHEN** an update attempts to change an unstarted Issue to unknown repository `missing` together with other Issue fields
- **THEN** the entire update SHALL be rejected
- **AND** the target repository and all other Issue fields SHALL remain unchanged

#### Scenario: Reject reassignment after execution starts

- **WHEN** an Issue has successfully started workflow execution
- **AND** a client attempts to change its target repository
- **THEN** the update SHALL be rejected with a conflict explaining that the target is locked
- **AND** the original target SHALL remain unchanged

#### Scenario: The lock survives terminal and reopened states

- **WHEN** an Issue that previously started later becomes `done` or `cancelled`, is archived, or is reopened
- **THEN** its target repository SHALL remain locked
- **AND** a target repository change SHALL be rejected

#### Scenario: A blocked start does not lock the target

- **WHEN** an Issue start is rejected before workflow execution begins because the Issue is draft or has an unmet prerequisite
- **THEN** the Issue SHALL remain eligible for target repository reassignment

#### Scenario: Clearing the current workflow reference does not unlock the target

- **WHEN** an Issue successfully started and its current workflow-run reference is later cleared after a stop or failure
- **THEN** the Issue's target repository SHALL remain locked

#### Scenario: Historical start evidence locks a recovered existing target

- **WHEN** an existing Issue has persisted evidence that workflow execution previously started and one target repository is successfully recovered
- **THEN** adoption of this capability SHALL preserve that Issue's target as locked
- **AND** the absence of an active workflow run MUST NOT make it eligible for reassignment

#### Scenario: Workspace preparation failure after recorded start keeps the lock

- **WHEN** a workflow start is successfully recorded but target workspace preparation later fails
- **THEN** the Issue's target repository SHALL remain locked
- **AND** retry or recovery SHALL continue to use the same target

### Requirement: Issue reads expose the stored target repository

Issue creation, list, and detail read results SHALL expose the canonical target repository name from the stored binding. When the repository remains declared, reads SHALL also resolve its current Project-managed metadata. When a terminal Issue retains a target whose declaration has been removed, reads SHALL continue to expose the stored target name together with an unresolved-repository condition and MUST NOT present the default repository as its target. Human-readable Issue detail surfaces, including `mo issue show` and the Web Issue detail, SHALL identify the target repository.

#### Scenario: Read a declared target repository

- **WHEN** an Issue is bound to repository `web` and `web` remains declared
- **THEN** list and detail reads SHALL identify `web` as the target
- **AND** resolved repository metadata SHALL come from the current `web` declaration

#### Scenario: Read a terminal Issue after repository deletion

- **WHEN** a terminal Issue remains bound to `web` after repository `web` is deleted
- **THEN** Issue detail SHALL still identify `web` as the stored target
- **AND** it SHALL report that the repository declaration is unresolved
- **AND** it MUST NOT display the Project default as the Issue target

#### Scenario: Human-readable detail identifies the target

- **WHEN** a user opens the Web Issue detail or runs `mo issue show <number>` in table mode
- **THEN** the displayed details SHALL include the Issue's target repository name

### Requirement: Issue lists filter by stored target repository

Issue listing SHALL accept a target-repository filter and return only Issues whose stored canonical target name matches the filter case-insensitively. The repository filter SHALL compose with existing status, stage, label, priority, and archive filters. Filtering MUST use the stored binding rather than the current default flag or resolved Git metadata, so a default change or removal of a terminal Issue's repository does not change historical membership.

#### Scenario: Filter a multi-repository Project

- **WHEN** a Project contains Issues bound to `server` and `web`
- **AND** the Issue list is filtered by repository `SERVER`
- **THEN** every returned Issue SHALL be bound to `server`
- **AND** no Issue bound to `web` SHALL be returned

#### Scenario: Compose repository and status filters

- **WHEN** the Issue list is filtered by repository `server` and status `in_progress`
- **THEN** every returned Issue SHALL satisfy both filters

#### Scenario: Filter historical terminal Issues

- **WHEN** terminal Issues retain target `web` after the `web` declaration is removed
- **AND** the Issue list is filtered by repository `web` with terminal and archived Issues included
- **THEN** those Issues SHALL remain discoverable by their stored target name

### Requirement: The Issue CLI uses the canonical `--repo` option

The Issue CLI SHALL use `--repo <name>` as the repository option for `mo issue create`, `mo issue update`, and `mo issue list`. Create SHALL select a target, update SHALL request an eligible reassignment, and list SHALL apply the target-repository filter. Omitting `--repo` during creation SHALL preserve default binding. The previous partial `--repository` option MUST NOT remain an accepted Issue option.

#### Scenario: Create through the CLI with a target

- **WHEN** a user runs `mo issue create "Web change" --repo web`
- **THEN** the created Issue SHALL be bound to repository `web`

#### Scenario: Update through the CLI with a target

- **WHEN** a user runs `mo issue update 42 --repo web` for an Issue that has never started
- **THEN** the CLI SHALL request target reassignment to `web`
- **AND** a successful result SHALL identify `web` as the new target

#### Scenario: Filter through the CLI

- **WHEN** a user runs `mo issue list --repo server`
- **THEN** the CLI SHALL display only Issues bound to repository `server`

#### Scenario: Reject the replaced CLI option

- **WHEN** a user passes `--repository` to an Issue command
- **THEN** command parsing SHALL fail with a non-zero exit status
- **AND** no Issue request SHALL be sent

### Requirement: Repository-aware creation surfaces follow the same default rule

Every supported Issue creation surface SHALL apply the same explicit-or-default target selection rule. A multi-repository Web creation form SHALL visibly select the Project's declared default repository rather than assuming declaration order, and SHALL allow a user to select another declared repository. A single-repository form SHALL require no repository decision from the user.

#### Scenario: Web creation starts on the declared default

- **WHEN** a Project declares `web` first but marks `server` as default
- **AND** the user opens the Web Issue creation form
- **THEN** `server` SHALL be the selected target

#### Scenario: Web creation selects a non-default repository

- **WHEN** the user selects declared repository `web` and creates the Issue
- **THEN** the created Issue SHALL be bound to `web`
