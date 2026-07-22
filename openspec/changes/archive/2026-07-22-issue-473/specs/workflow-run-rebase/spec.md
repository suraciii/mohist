### Requirement: Rebase task payload conforms to the declared Action inputs

The ad-hoc rebase task constructed for an operator-triggered rebase SHALL hand the `mohist/rebase` Action a `with` payload that carries only inputs the `mohist/rebase` Action declares in its manifest. The payload MUST NOT include a `repository` field, and the run-owned repository context (name, git URL, base branch) MUST NOT be mirrored into the Action's inputs.

#### Scenario: Rebase task carries only declared inputs

- **WHEN** an operator triggers a rebase on a workflow run that has repository context
- **THEN** the constructed rebase task's `with` payload SHALL populate `baseBranch` and `remote`, and MUST NOT contain a `repository` key

#### Scenario: Rebase dispatches without input-validation failure

- **WHEN** the rebase task is dispatched to the `mohist/rebase` Action
- **THEN** the dispatch-time Action input validation SHALL accept the payload, and the rebase Action SHALL proceed to run its git fetch and git rebase steps, reporting either a successful rebase or a conflict instead of an `invalid-input` / `unknown input` rejection

### Requirement: Omitted base branch defaults from the run-owned repository context

When the operator does not name a base branch, the rebase task's `baseBranch` input SHALL be resolved from the workflow run's owned repository snapshot. The run-owned repository snapshot SHALL remain available for this defaulting even though it is no longer copied into the Action's `with` payload.

#### Scenario: No explicit base branch uses the run snapshot

- **WHEN** an operator triggers a rebase without specifying a base branch on a run whose repository snapshot records base branch `release`
- **THEN** the rebase task's `baseBranch` input SHALL equal `release`

#### Scenario: Explicit base branch overrides the default

- **WHEN** an operator triggers a rebase specifying an explicit base branch
- **THEN** the rebase task's `baseBranch` input SHALL equal the operator-provided base branch, regardless of the run-owned snapshot's base branch

### Requirement: Missing repository context rejects the rebase before queueing

A rebase requested on a workflow run that has no repository context SHALL be rejected before any rebase task is queued onto the workflow run.

#### Scenario: Run without repository context is rejected

- **WHEN** an operator triggers a rebase on a run with no repository context
- **THEN** the system SHALL reject the request with a `missing_repository_context` error
- **AND** the system MUST NOT queue a rebase task onto the run

### Requirement: Rebase conflict recovery behavior is unchanged

The payload conformance fix SHALL NOT alter the conflict-recovery behavior attached to the rebase task. A rebase that reports a conflict SHALL still trigger the existing `recover:resolve-rebase-conflicts` recovery task, and that recovery task SHALL continue to be constructed unchanged.

#### Scenario: Conflict triggers the existing recovery task

- **WHEN** the dispatched `mohist/rebase` task reports a conflict
- **THEN** the system SHALL invoke the `recover:resolve-rebase-conflicts` recovery task, whose `Uses`, `With`, and handler condition SHALL match the pre-change contract
