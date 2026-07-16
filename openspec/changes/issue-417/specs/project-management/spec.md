### Requirement: Repository deletion preserves non-terminal Issue bindings

A Project repository SHALL be deletable only when it exists, is not the Project default, and no Issue in that same Project is bound to it in a non-terminal status. For this rule, `backlog` and `in_progress` are non-terminal, while `done` and `cancelled` are terminal. Draft and archive state, workflow runtime state, and the presence or absence of a current workflow-run reference MUST NOT redefine terminality. Binding comparison SHALL use the stored canonical repository name case-insensitively.

#### Scenario: Delete an unused non-default repository

- **WHEN** repository `web` is non-default and no non-terminal Issue in its Project is bound to it
- **THEN** deletion SHALL remove `web`
- **AND** the Project's existing default repository SHALL remain unchanged

#### Scenario: A backlog Issue blocks deletion

- **WHEN** a `backlog` Issue is bound to non-default repository `web`
- **THEN** deletion of `web` SHALL be rejected as an in-use conflict
- **AND** the result SHALL be the same when the Issue is a draft

#### Scenario: An in-progress Issue blocks deletion regardless of workflow state

- **WHEN** an `in_progress` Issue is bound to non-default repository `web`
- **THEN** deletion of `web` SHALL be rejected
- **AND** a paused, stopped, failed, or otherwise terminal workflow runtime MUST NOT release the guard while the Issue remains `in_progress`

#### Scenario: Terminal Issues do not block deletion

- **WHEN** every Issue bound to non-default repository `web` is `done` or `cancelled`
- **THEN** deletion of `web` SHALL be allowed
- **AND** each terminal Issue SHALL retain `web` as its historical target name
- **AND** no Issue SHALL be silently retargeted to the default repository

#### Scenario: Issues in another Project do not block deletion

- **WHEN** another Project has a non-terminal Issue bound to its own repository named `web`
- **AND** the target Project has no non-terminal Issue bound to its `web` repository
- **THEN** deletion in the target Project SHALL be allowed

### Requirement: Binding and status changes update repository protection

The repository deletion guard SHALL reflect the latest successfully committed Issue binding and status. Reassigning an eligible unstarted Issue SHALL release its old target and protect its new target. Transitioning an Issue to `done` or `cancelled` SHALL release its target without erasing the historical binding. Reopening a cancelled Issue SHALL protect its retained target again, and MUST be rejected if that target is no longer declared unless the Issue is first eligible for and receives a valid reassignment.

#### Scenario: Reassignment moves the deletion guard

- **WHEN** the last non-terminal Issue bound to `server` is successfully reassigned to `web`
- **THEN** `server` SHALL no longer be protected by that Issue
- **AND** `web` SHALL be protected by that Issue

#### Scenario: One remaining Issue keeps the repository protected

- **WHEN** two non-terminal Issues are bound to `web`
- **AND** one is reassigned or enters a terminal status
- **THEN** deletion of `web` SHALL remain blocked by the other non-terminal Issue

#### Scenario: Reopen restores protection

- **WHEN** a cancelled Issue bound to declared repository `web` is reopened to `backlog`
- **THEN** `web` SHALL again be protected from deletion by that Issue

#### Scenario: Reject reopen after target deletion

- **WHEN** a cancelled Issue retains target `web` after `web` has been deleted
- **AND** no valid target reassignment has been made
- **THEN** reopening the Issue SHALL be rejected with an error identifying the missing target
- **AND** the Issue SHALL remain `cancelled`

### Requirement: Repository deletion conflicts are atomic and actionable

An attempted deletion of a repository used by non-terminal Issues SHALL return an HTTP conflict with stable code `repository_in_use_deletion_conflict`. The conflict SHALL identify the Project, repository, and blocking Issue numbers and SHALL leave repository membership, default selection, and all Issue state unchanged. Deleting the default repository SHALL continue to return `repository_default_deletion_conflict` before evaluating whether it is also in use. Deleting an unknown repository SHALL return a not-found result without mutation. Repository management surfaces, including Web settings, SHALL retain the repository after any conflict and SHALL expose the failure rather than reporting successful deletion.

#### Scenario: In-use deletion returns a stable conflict

- **WHEN** repository `web` is bound by non-terminal Issues 12 and 19
- **AND** a client requests deletion of `web`
- **THEN** the server SHALL return HTTP 409 with code `repository_in_use_deletion_conflict`
- **AND** the error SHALL identify `web`, its Project, and blocking Issues 12 and 19
- **AND** no Project or Issue state SHALL change

#### Scenario: Default deletion conflict takes precedence

- **WHEN** repository `server` is both default and bound by non-terminal Issues
- **AND** a client requests deletion of `server`
- **THEN** the server SHALL return code `repository_default_deletion_conflict`
- **AND** it SHALL instruct the caller to select another default first
- **AND** no repository SHALL be deleted

#### Scenario: Unknown repository deletion remains not found

- **WHEN** a client requests deletion of a repository name the Project does not declare
- **THEN** the server SHALL return a not-found result identifying that repository
- **AND** the Project SHALL remain unchanged

#### Scenario: Web settings preserves an in-use repository

- **WHEN** a user attempts to delete an in-use repository from Web repository settings
- **THEN** the Web surface SHALL display the in-use conflict
- **AND** the repository SHALL remain visible in the Project repository list
- **AND** the Web surface MUST NOT report successful deletion

### Requirement: Concurrent binding and deletion cannot create an orphan

Issue creation, reassignment, reopen, and repository deletion MUST NOT create a new state in which a non-terminal Issue is bound to a repository no longer declared by its Project. Each of these Issue operations racing deletion SHALL produce a serially valid outcome: either the non-terminal binding commits and deletion is rejected, or deletion commits and the competing Issue operation is rejected because the target is unknown. Both operations MUST NOT succeed if doing so would leave an orphaned non-terminal Issue.

#### Scenario: Creation races repository deletion

- **WHEN** Issue creation targeting `web` races deletion of repository `web`
- **THEN** at most one operation SHALL commit when both commits would violate the binding invariant
- **AND** the final state MUST NOT contain a non-terminal Issue bound to an undeclared repository

#### Scenario: Reassignment races repository deletion

- **WHEN** reassignment of an unstarted Issue to `web` races deletion of repository `web`
- **THEN** the final state SHALL either retain `web` with the reassigned Issue or delete `web` with the Issue's prior binding unchanged
- **AND** the final state MUST NOT contain an orphaned non-terminal binding

#### Scenario: Reopen races repository deletion

- **WHEN** reopening a cancelled Issue bound to `web` races deletion of repository `web`
- **THEN** the final state SHALL either retain `web` with the Issue reopened to `backlog` or delete `web` with the Issue remaining `cancelled`
- **AND** both operations MUST NOT commit if that would leave a reopened Issue bound to deleted repository `web`
