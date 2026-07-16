### Requirement: Repository deletion preserves non-terminal Issue bindings

A Project repository SHALL be deletable only when it exists, is not the Project default, and no non-terminal Issue in that Project is bound to it. `backlog` and `in_progress` Issues SHALL block deletion; `done` and `cancelled` Issues SHALL NOT block deletion. A deletion rejected because of non-terminal bindings SHALL report an in-use conflict identifying the repository and SHALL leave Project and Issue state unchanged. Successful deletion MUST NOT rewrite historical Issue bindings or select a replacement target for them.

#### Scenario: Delete an unused non-default repository

- **WHEN** repository `web` is non-default and no non-terminal Issue in its Project is bound to it
- **THEN** deletion SHALL remove `web` without changing the Project's default repository or any Issue binding

#### Scenario: A backlog Issue blocks deletion

- **WHEN** a `backlog` Issue, including a draft Issue, is bound to non-default repository `web`
- **THEN** deletion of `web` SHALL fail as an in-use conflict and no state SHALL change

#### Scenario: An in-progress Issue blocks deletion

- **WHEN** an `in_progress` Issue is bound to non-default repository `web`
- **THEN** deletion of `web` SHALL fail regardless of whether its workflow is active, paused, stopped, or failed

#### Scenario: Terminal Issues do not block deletion

- **WHEN** every Issue bound to non-default repository `web` is `done` or `cancelled`
- **THEN** deletion of `web` SHALL succeed and each terminal Issue SHALL retain `web` as its historical target name

#### Scenario: Another Project does not block deletion

- **WHEN** another Project has a non-terminal Issue bound to its own repository named `web` but the selected Project has no such binding
- **THEN** deletion of the selected Project's non-default `web` repository SHALL be allowed

#### Scenario: The default repository remains protected

- **WHEN** a repository is the Project default, whether or not Issues are bound to it
- **THEN** deletion SHALL remain rejected until another repository is selected as default

### Requirement: Repository protection follows committed Issue state

The deletion guard SHALL reflect each Issue's latest committed target binding and status. Successful reassignment of an unstarted Issue SHALL release its former target and, when the Issue is non-terminal, protect its new target. Transition to `done` or `cancelled` SHALL release the target without erasing the binding. Reopening a cancelled Issue SHALL protect its retained target again and SHALL be rejected if that target is no longer declared.

#### Scenario: Reassignment moves repository protection

- **WHEN** the last non-terminal Issue bound to `server` is successfully reassigned to `web`
- **THEN** that Issue SHALL stop protecting `server` and SHALL protect `web`

#### Scenario: One remaining Issue keeps protection

- **WHEN** two non-terminal Issues are bound to `web` and only one is reassigned or becomes terminal
- **THEN** deletion of `web` SHALL remain blocked by the other non-terminal Issue

#### Scenario: Terminal transition releases protection

- **WHEN** the final non-terminal Issue bound to `web` becomes `done` or `cancelled`
- **THEN** that Issue SHALL no longer block deletion of non-default repository `web`

#### Scenario: Reopening restores protection

- **WHEN** a cancelled Issue bound to declared repository `web` is reopened to `backlog`
- **THEN** repository `web` SHALL again be protected from deletion by that Issue

#### Scenario: Reopening cannot restore a missing target

- **WHEN** a cancelled Issue retains target `web` after `web` was deleted and a caller attempts to reopen it
- **THEN** reopening SHALL fail and the Issue SHALL remain cancelled

### Requirement: Binding changes and deletion cannot create an orphan

Issue creation, target reassignment, reopening, and repository deletion MUST preserve the invariant that every non-terminal Issue is bound to a repository currently declared by its Project. When deletion races an Issue operation that would establish a non-terminal binding, the resulting committed state SHALL be equivalent to an order in which either the binding is established and deletion fails, or deletion succeeds and the competing Issue operation fails. Both operations MUST NOT succeed when that would leave an orphaned non-terminal binding.

#### Scenario: Creation races repository deletion

- **WHEN** Issue creation targeting `web` races deletion of repository `web`
- **THEN** the final state SHALL either retain `web` with the new Issue or delete `web` without creating that Issue, and MUST NOT contain an orphaned binding

#### Scenario: Reassignment races repository deletion

- **WHEN** reassignment of a non-terminal unstarted Issue to `web` races deletion of repository `web`
- **THEN** the final state SHALL either retain `web` with the reassigned Issue or delete `web` with the Issue's previous binding unchanged

#### Scenario: Reopen races repository deletion

- **WHEN** reopening a cancelled Issue bound to `web` races deletion of repository `web`
- **THEN** the final state SHALL either retain `web` with the Issue reopened or delete `web` with the Issue still cancelled
