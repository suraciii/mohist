# OpenSpec Capability: workspace-branch-stability

### Requirement: Workflow workspace stays on its run branch for the whole run

A workflow-owned workspace SHALL remain checked out to its run branch, exposed as `workspace.branch`, for the entire lifetime of the workflow run. The repository base branch (such as `master`) is a ref to fetch, rebase onto, and push against; it SHALL NOT be checked out inside the workflow workspace. Workflow actions MAY fetch refs, rebase the run branch onto the remote base, inspect refs, create isolated temporary workspaces outside the workflow workspace, and push refs, but any operation that needs a different branch context SHALL happen in an isolated temporary workspace, not by switching the workflow workspace.

#### Scenario: Workspace remains on the run branch across the run

- **GIVEN** a workflow-owned workspace has been created for a run
- **WHEN** any workflow task or integration action executes against that workspace
- **THEN** the workspace SHALL be on its `workspace.branch` before, during, and after the action
- **AND** the workflow workspace SHALL NOT be checked out to the repository base branch at any point during the run

#### Scenario: Base branch is a ref-only target inside the workflow workspace

- **WHEN** an action needs the latest base branch state inside the workflow workspace
- **THEN** the action SHALL fetch the remote base ref and operate against `refs/remotes/<remote>/<baseBranch>` or an equivalent ref
- **AND** the action SHALL NOT run `git checkout <baseBranch>` inside the workflow workspace

#### Scenario: Branch-changing operations use an isolated temporary workspace

- **WHEN** an action needs to construct a commit, merge, or landing on a branch other than the run branch
- **THEN** the action SHALL create an isolated temporary workspace separate from the workflow workspace
- **AND** the workflow workspace SHALL remain on its `workspace.branch` for the duration of that operation

### Requirement: Task boundary branch verification records branch-stability evidence

Workflow task execution SHALL verify the workflow workspace is on the expected `workspace.branch` before a task starts and after a task ends. The verified branch SHALL be recorded as branch-stability evidence alongside clean-worktree evidence. A task whose workspace is not on the expected run branch at a boundary SHALL NOT proceed past the start boundary or be reported as completed past the end boundary.

#### Scenario: Task start records the run branch

- **WHEN** workflow task execution begins a task against the workflow workspace
- **THEN** it SHALL verify the workspace is on `workspace.branch`
- **AND** it SHALL record that branch as branch-stability start evidence for the task attempt

#### Scenario: Task end records the run branch

- **WHEN** a task attempt reports a result against the workflow workspace
- **THEN** workflow task execution SHALL verify the workspace is still on `workspace.branch`
- **AND** it SHALL record that branch as branch-stability end evidence for the task attempt
- **AND** a task whose workspace left the run branch SHALL NOT be treated as completed

#### Scenario: Start boundary on the wrong branch blocks the task

- **WHEN** a task attempt is about to start
- **AND** the workflow workspace is on a branch other than `workspace.branch`
- **THEN** the task SHALL NOT execute its work
- **AND** the runner SHALL report a branch-invariant violation with the observed branch and the expected branch

### Requirement: Branch-invariant violations are surfaced as distinct runner/action bugs

A workflow action that starts or ends a task on a branch other than the expected `workspace.branch` SHALL be surfaced as a branch-invariant-violation runner/action bug with clear evidence, distinct from dirty-worktree, conflict, base-moved, and provider failures. Retrying a failed task SHALL NOT require manually restoring the workflow workspace from the base branch back to the run branch.

#### Scenario: Wrong branch at a task boundary is a branch-invariant violation

- **WHEN** a task boundary check observes a branch other than `workspace.branch`
- **THEN** the failure SHALL be reported with a branch-invariant-violation kind
- **AND** the evidence SHALL include the expected branch and the observed branch
- **AND** the failure SHALL be attributed to the runner or action, not to issue work

#### Scenario: Branch-invariant violation is distinct from a dirty worktree

- **WHEN** a task boundary check observes the wrong branch
- **THEN** the failure SHALL NOT be reported as a dirty-worktree, conflict, base-moved, or provider failure
- **AND** dirty-worktree evidence SHALL only be reported when the branch matches `workspace.branch` and `git status --porcelain` is non-empty

#### Scenario: Retry recovers without manual branch restore

- **GIVEN** a task failed with a branch-invariant violation or a delivery failure left the workspace on the wrong branch
- **WHEN** the failed task is retried
- **THEN** the workflow workspace SHALL be brought back to `workspace.branch` by the runner as part of retry
- **AND** the user SHALL NOT be required to manually restore the workspace from the base branch before retry can proceed