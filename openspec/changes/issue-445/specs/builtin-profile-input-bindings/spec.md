### Requirement: Bundled profiles bind affected Action inputs explicitly

The `mohist/local` and `mohist/github-pr` profile definitions SHALL provide every required input for their built-in Actions through `with`. Repository and workspace runtime context SHALL be bound with the existing `${{ repository.* }}` and `${{ workspace.* }}` expressions, and workflow-produced values SHALL be bound with `${{ vars.* }}` only where the profile intentionally consumes Variables.

#### Scenario: Workspace preparation receives its expected branch
- **WHEN** either bundled profile dispatches a `mohist/workspace-prepare` task
- **THEN** the task's rendered `with` payload SHALL contain the expected workspace branch required by the Action
- **AND** the Action MUST NOT require `variables.workspace.branch` to prepare the workspace

#### Scenario: Delivery tasks expose repository and branch routing
- **WHEN** either bundled profile dispatches a Git or GitHub delivery task or check
- **THEN** its `with` definition SHALL visibly bind every required repository selector, source branch, target branch, remote, and pull request input used by that operation

#### Scenario: Stored PR identity is consumed explicitly
- **WHEN** the GitHub PR profile uses the pull request identity produced by the draft-PR task in a later stage
- **THEN** the later task or check SHALL bind `vars.github.pr.number` explicitly to its declared pull request input
- **AND** the receiving Action MUST NOT discover that number directly from Run Variables

### Requirement: Local profile behavior is preserved with explicit inputs

After its bindings are made explicit, `mohist/local` SHALL retain its existing workflow stages, approval gates, recovery behavior, and direct-delivery outcome. Its merge-readiness check SHALL evaluate the workflow branch against the repository base branch, and integration SHALL rebase and squash the workflow branch before pushing it to the repository base branch.

#### Scenario: Local workflow completes direct delivery
- **WHEN** a `mohist/local` run has valid repository and workspace context and all tasks, checks, and approvals succeed
- **THEN** the workflow SHALL complete the plan, build, check, and integrate stages in their existing order
- **AND** integration SHALL deliver the squashed workflow changes directly to the explicitly bound repository base branch

#### Scenario: Local merge-readiness recovery keeps explicit routing
- **WHEN** the local merge-readiness task reports that the explicitly bound workflow branch does not contain the explicitly bound base branch
- **THEN** the existing rebase recovery SHALL run with explicit base branch and remote inputs
- **AND** retrying merge readiness SHALL use the same explicit routing values

### Requirement: GitHub PR profile behavior is preserved with explicit inputs

After its bindings are made explicit, `mohist/github-pr` SHALL retain its draft-PR, review-ready, and squash-merge delivery flow. The profile SHALL publish the workflow branch before PR operations, persist the created PR number through task output projection, use that explicitly bound number for later PR operations and checks, and retain the existing recovery paths for base movement and failed PR checks.

#### Scenario: GitHub PR workflow completes PR delivery
- **WHEN** a `mohist/github-pr` run has valid repository and workspace context and all tasks, checks, GitHub checks, and approvals succeed
- **THEN** plan SHALL publish the workflow branch and open or reuse the draft PR using explicit delivery inputs
- **AND** check SHALL publish reviewed changes and mark the explicitly identified PR ready
- **AND** integrate SHALL publish archived changes and squash-merge that PR

#### Scenario: GitHub PR status checks use explicit identity
- **WHEN** the profile checks that its PR is ready or merged
- **THEN** `mohist/github-pr-status` SHALL receive the PR number in rendered `with`
- **AND** the status Action MUST NOT fall back to `variables.github.pr.number`

#### Scenario: Merge recovery preserves explicit delivery inputs
- **WHEN** GitHub PR merge recovery rebases or republishes the workflow branch after a base movement or failed PR checks
- **THEN** every recovery delivery Action SHALL receive its required source, target, remote, and repository inputs through `with`
- **AND** retrying the merge SHALL continue to use the explicitly bound PR identity
