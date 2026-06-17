## ADDED Requirements

### Requirement: Merge validates source worktree is clean before starting

The `mohist/merge` action SHALL check that the source worktree is clean before performing any merge operations. It SHALL refuse to merge if `git status --porcelain` in the worktree is non-empty, regardless of whether the dirty state is from the source branch or from leftover changes from earlier tasks.

#### Scenario: Dirty source worktree blocks merge

- **WHEN** `mohist/merge` starts execution
- **AND** `git status --porcelain` in the merge worktree returns non-empty output
- **THEN** the merge action SHALL fail immediately
- **AND** the failure output SHALL include structured dirty-worktree evidence listing staged, unstaged, and untracked files
- **AND** the merge action SHALL NOT checkout, fetch, rebase, or push any branch

#### Scenario: Clean source worktree allows merge to proceed

- **WHEN** `mohist/merge` starts execution
- **AND** `git status --porcelain` in the merge worktree returns empty output
- **THEN** the merge action SHALL proceed to the next phase of the delivery flow

### Requirement: Merge fetches latest remote target before rebasing

The `mohist/merge` action SHALL fetch the latest remote target branch before rebasing the source branch. The fetched remote target commit SHALL be the base for rebase and the parent for the squash landing commit.

#### Scenario: Remote target is fetched

- **WHEN** `mohist/merge` passes the clean source worktree guard
- **THEN** it SHALL run `git fetch <remote> <target>` to retrieve the latest remote target commit
- **AND** the fetched commit SHALL be recorded as the base for subsequent operations

#### Scenario: Fetch failure blocks merge

- **WHEN** `mohist/merge` attempts to fetch the remote target
- **AND** the fetch fails due to network, auth, or repository errors
- **THEN** the merge action SHALL fail
- **AND** the failure SHALL identify the phase as `fetch` with structured error evidence

### Requirement: Merge rebases source branch onto fetched remote target

The `mohist/merge` action SHALL rebase the source branch onto the latest fetched remote target commit before creating the squash landing commit. Rebase conflicts SHALL be resolved by the configured agent resolver inside the merge action.

#### Scenario: Clean rebase succeeds

- **WHEN** `mohist/merge` rebases the source branch onto the fetched remote target
- **AND** the rebase completes without conflicts
- **THEN** the merge action SHALL verify the source worktree is clean after rebase
- **AND** it SHALL proceed to create the squash landing commit

#### Scenario: Rebase conflict invokes agent resolver

- **WHEN** `mohist/merge` attempts to rebase the source branch
- **AND** the rebase produces conflicts
- **THEN** the merge action SHALL invoke the configured conflict resolver agent
- **AND** the agent SHALL receive the list of conflicted files and resolution instructions
- **AND** the merge action SHALL NOT create a squash landing commit until the rebase is complete and the worktree is clean

#### Scenario: Rebase conflict resolution succeeds

- **WHEN** the conflict resolver agent resolves all rebase conflicts
- **AND** `git status --porcelain` shows no conflict markers and no uncommitted changes
- **THEN** the merge action SHALL proceed to create the squash landing commit
- **AND** the result SHALL record the number of conflict resolution attempts

#### Scenario: Rebase conflict resolution exhausted

- **WHEN** the conflict resolver agent exhausts the configured maximum retry attempts
- **AND** rebase conflicts remain unresolved
- **THEN** the merge action SHALL fail
- **AND** the failure SHALL identify the phase as `rebase-conflict` with the list of unresolved conflict files

### Requirement: Merge creates one squash landing commit from remote target HEAD

The `mohist/merge` action SHALL create exactly one squash landing commit whose parent is the fetched remote target commit. The landing commit SHALL be created from a temporary landing HEAD or landing branch, not from an arbitrary local branch.

#### Scenario: Squash landing commit has correct parent

- **WHEN** `mohist/merge` creates the squash landing commit
- **THEN** the landing commit SHALL have exactly one parent
- **AND** that parent SHALL be the fetched remote target commit SHA
- **AND** the landing commit message SHALL match the configured merge message

#### Scenario: Landing commit is created from remote target HEAD

- **WHEN** `mohist/merge` prepares the landing commit
- **THEN** it SHALL start from the fetched remote target commit, not from an arbitrary local branch
- **AND** it SHALL squash the changes from the rebased source branch into a single commit

#### Scenario: Landing worktree is validated after commit

- **WHEN** the squash landing commit is created
- **THEN** the merge action SHALL verify `git status --porcelain` is empty
- **AND** it SHALL verify no merge or rebase is in progress
- **AND** it SHALL verify the landing commit parent matches the fetched remote target commit

#### Scenario: Landing commit validation fails

- **WHEN** validation after landing commit creation detects dirty worktree, an in-progress merge or rebase, or an incorrect parent commit
- **THEN** the merge action SHALL fail
- **AND** the failure SHALL identify the phase as `landing-validation` with structured evidence

### Requirement: Merge fast-forward pushes the landing commit when push is enabled

When the `mohist/merge` action is configured with `push: true`, it SHALL push the exact landing commit to the remote target branch as a fast-forward update. It SHALL NOT force push, and it SHALL verify the remote target ref after the push.

#### Scenario: Fast-forward push succeeds

- **WHEN** `mohist/merge` has created a valid squash landing commit
- **AND** `push` is configured as `true`
- **THEN** the merge action SHALL push the landing commit to `<remote>/<target>` as a fast-forward update
- **AND** it SHALL NOT use `--force` or `--force-with-lease`

#### Scenario: Remote ref is verified after push

- **WHEN** `mohist/merge` completes the push
- **THEN** it SHALL verify that the remote target ref points to the landing commit or contains it as the new head
- **AND** the delivery facts SHALL include the verified remote ref and the landing commit SHA

#### Scenario: Push is skipped when push is not enabled

- **WHEN** `mohist/merge` has created a valid squash landing commit
- **AND** `push` is not configured as `true`
- **THEN** the merge action SHALL report success without pushing
- **AND** the delivery facts SHALL include the landing commit SHA but no remote ref verification

### Requirement: Merge retries on remote-advanced race

If the remote target branch advances between the initial fetch and the fast-forward push, the merge action SHALL fetch the new remote target, rebase the source again, regenerate the squash landing commit, and retry the push within a bounded limit.

The default `maxPushRetry` is **5**, meaning the merge action runs at most 5 fetch→rebase→land→push cycles before giving up. Operators MAY lower or raise the bound by setting `maxPushRetry` on the `mohist/merge` action's `with` block.

#### Scenario: Push retry bound defaults to five

- **WHEN** the default Integrate workflow runs
- **THEN** `mohist/merge` SHALL be configured with `maxPushRetry: 5`
- **AND** a remote-advanced race SHALL trigger at most 5 retry cycles

#### Scenario: Push retry bound is overridable

- **WHEN** `mohist/merge` is configured with a different `maxPushRetry` value
- **THEN** the merge action SHALL use that value as the maximum number of push attempts

#### Scenario: Remote target advanced triggers retry

- **WHEN** `mohist/merge` attempts a fast-forward push
- **AND** the push is rejected because the remote target has advanced since the last fetch
- **THEN** the merge action SHALL fetch the new remote target commit
- **AND** it SHALL rebase the source branch onto the new remote target commit
- **AND** it SHALL regenerate the squash landing commit
- **AND** it SHALL retry the fast-forward push

#### Scenario: Remote-advanced retry succeeds

- **WHEN** the merge action retries after a remote-advanced race
- **AND** the fetch, rebase, and push all succeed on the retry
- **THEN** the merge action SHALL report success
- **AND** the delivery facts SHALL include the retry attempt count

#### Scenario: Remote-advanced retry limit exhausted

- **WHEN** the merge action exhausts the configured maximum push retry attempts
- **AND** each retry encounters a remote-advanced race
- **THEN** the merge action SHALL fail
- **AND** the failure SHALL identify the phase as `push` with structured evidence including the number of retry attempts and the last known remote target commit

### Requirement: Merge failure evidence identifies the failing phase

When `mohist/merge` fails at any point in the delivery flow, the failure output SHALL identify the phase at which the failure occurred so that users and automated recovery paths can determine the correct remediation action.

#### Scenario: Each phase produces distinct failure classification

- **WHEN** `mohist/merge` fails
- **THEN** the failure output SHALL include a `phase` field that is one of `source-cleanup`, `fetch`, `rebase-conflict`, `landing-validation`, or `push`
- **AND** the failure message SHALL describe the phase-specific failure reason
- **AND** CLI and issue detail surfaces SHALL display the phase classification alongside the failure message
