## REMOVED Requirements

### Requirement: Integrate delivery is prepare then publish

**Reason:** The two-task `integrate:prepare` → `integrate:publish` delivery model is replaced by an on-workspace `integrate:rebase` → `integrate:push` flow. The `mohist/prepare` and `mohist/publish` actions are deleted, and the disposable landing clone that publish relied on is removed.

**Migration:** `integrate:prepare` is replaced by `integrate:rebase` (unified rebase action with `remote=origin` and `squash=true`), and `integrate:publish` is replaced by `integrate:push` (a fast-forward `git push origin <runBranch>:<baseBranch>`). See the ADDED "Integrate delivery is rebase then push" requirement.

### Requirement: Prepare reconciles the issue branch with the base branch

**Reason:** The `mohist/prepare` action is deleted. Its remote-fetch rebase and conflict-resolution behavior is absorbed into the unified `mohist/rebase` action, which also performs the squash that publish used to own.

**Migration:** Rebase + conflict resolution now live in the ADDED "Rebase reconciles and squashes the run branch onto the base branch" requirement, executed by `integrate:rebase` on the single workflow workspace.

### Requirement: Publish lands one commit and pushes to the remote

**Reason:** The `mohist/publish` action and its isolated landing clone are deleted. The single-commit outcome is now produced by the rebase task's squash, and the remote landing is produced by a fast-forward push.

**Migration:** Landing one commit is split into rebase's squash (ADDED "Rebase reconciles and squashes the run branch onto the base branch") and push's fast-forward remote update (ADDED "Push fast-forwards the prepared run branch to the remote base"), executed by `integrate:push`.

## ADDED Requirements

### Requirement: Integrate delivery is rebase then push

The Integrate delivery SHALL be executed as two ordered, independently visible tasks: `integrate:rebase` followed by `integrate:push`. Each task SHALL be a genuine, independently tracked unit of work that appears in the task list with its own title, status, attempts, and result evidence. The delivery SHALL NOT be performed by a single opaque merge task, and conflict resolution SHALL NOT run hidden inside another task. Both tasks SHALL operate on the single workflow workspace on its `workspace.branch` and SHALL NOT create or use a disposable landing clone.

#### Scenario: Delivery appears as two visible on-workspace tasks

- **WHEN** an issue reaches the delivery portion of Integrate
- **THEN** the task list SHALL show `integrate:rebase` and `integrate:push` as separate ordered tasks
- **AND** each task SHALL carry its own title, status, attempts, and result evidence
- **AND** neither task SHALL create an isolated landing clone

#### Scenario: Push runs only after rebase succeeds

- **WHEN** `integrate:rebase` has not reached a successful terminal state
- **THEN** `integrate:push` SHALL NOT execute
- **AND** a failed rebase SHALL block push through ordinary task-failure semantics

### Requirement: Rebase reconciles and squashes the run branch onto the base branch

The `integrate:rebase` task SHALL bring the run branch up to date with the latest base branch by fetching the remote base ref and rebasing `workspace.branch` onto `refs/remotes/<remote>/<baseBranch>`, and SHALL resolve any conflicts that arise. After a successful rebase, when squash is requested, the task SHALL fold the run branch's commits into a single commit via `git reset --soft <base>` followed by `git commit` with the configured message, preserving the already-final work tree and index. Rebase SHALL stay on the run branch for its entire execution: it SHALL fetch the remote base ref, rebase `workspace.branch` onto it, resolve conflicts in place, and squash in place, and it SHALL NOT check out the base branch inside the workflow workspace. This task SHALL be the single place where conflict resolution happens during delivery; the conflict-resolution work SHALL be attributable to the rebase task's own attempts and evidence so it can be seen and retried on its own. The rebase task SHALL NOT push to the remote. The squash phase SHALL run only after a successful rebase, so it SHALL NOT produce conflicts or introduce new failure modes.

#### Scenario: Clean branch rebases and squashes without conflict resolution

- **WHEN** the run branch can be brought up to date with the base branch without conflicts
- **THEN** `integrate:rebase` SHALL complete the rebase successfully while staying on `workspace.branch`
- **AND** when squash is requested it SHALL fold the run branch's commits into a single commit
- **AND** the task result SHALL record the base commit it rebased onto and the resulting squashed head
- **AND** the rebase task SHALL NOT check out the base branch inside the workflow workspace

#### Scenario: Conflicting branch resolves conflicts as visible rebase work

- **WHEN** bringing the run branch up to date with the base branch produces conflicts
- **THEN** `integrate:rebase` SHALL resolve the conflicts as part of the rebase task while staying on `workspace.branch`
- **AND** the conflict-resolution work SHALL be attributable to the rebase task's attempts and evidence
- **AND** no other delivery task SHALL perform conflict resolution

#### Scenario: Rebase never checks out the base branch

- **WHEN** `integrate:rebase` fetches, rebases, resolves conflicts, or squashes
- **THEN** the workspace SHALL remain on `workspace.branch` throughout
- **AND** rebase SHALL operate against the remote base ref without checking out the base branch inside the workflow workspace

#### Scenario: Squash cannot introduce new conflicts

- **WHEN** rebase succeeds and the squash phase runs `git reset --soft <base>` followed by `git commit`
- **THEN** the squash SHALL operate on the already-rebased final work tree and index
- **AND** the squash phase SHALL NOT produce merge conflicts or a new failure surface

### Requirement: Push fast-forwards the prepared run branch to the remote base

The `integrate:push` task SHALL land the prepared issue changes on the base branch as a fast-forward ref update by running `git push origin <source>:<target>` with the run branch as `source` and the base branch as `target`. Push SHALL NOT check out the base branch inside the workflow workspace, SHALL NOT create or use an isolated landing clone, and SHALL NOT mutate the workflow workspace's working tree or index. The push task SHALL be the single owner for pushing to the remote; the rebase task SHALL NOT push. After a successful rebase-with-squash, an issue's changes SHALL land on the base branch as exactly one commit, preserving the existing user-visible single-commit delivery outcome.

#### Scenario: Push lands the prepared commit as a fast-forward

- **WHEN** `integrate:push` runs after a successful rebase (with squash)
- **THEN** the prepared run branch head SHALL be pushed to the remote base branch via `git push origin <runBranch>:<baseBranch>`
- **AND** the remote base branch SHALL advance as a fast-forward to the prepared commit
- **AND** the task result SHALL record the landed commit and that the push occurred

#### Scenario: Push lands without leaving the run branch or mutating the workspace

- **WHEN** `integrate:push` pushes the prepared run branch to the remote base branch
- **THEN** the workflow workspace SHALL remain on `workspace.branch` for the entire push task
- **AND** push SHALL NOT run `git checkout <baseBranch>` inside the workflow workspace
- **AND** push SHALL NOT create an isolated landing clone or mutate the workspace's working tree

## MODIFIED Requirements

### Requirement: Delivery failures are classified into actionable kinds

A failed `integrate:rebase` or `integrate:push` task SHALL report a failure kind that tells the user the nature of the problem and implies the next action. The delivery SHALL distinguish at least four failure kinds: a failure that is safe to retry as-is, a failure caused by the base branch having moved so the branch needs preparing again, a failure caused by a conflict that needs attention, and a branch-invariant violation caused by the workspace leaving the expected run branch. A branch-invariant violation SHALL be attributed to the runner or action, not to issue work. The `integrate:rebase` task SHALL report `conflict` and `retry-safe` kinds; the `integrate:push` task SHALL report `base-moved` and `retry-safe` kinds.

#### Scenario: Retry-safe failure kind is reported

- **WHEN** a delivery task fails for a transient reason unrelated to base movement, conflicts, or branch stability
- **THEN** the failure SHALL be reported with a retry-safe kind
- **AND** the indicated user action SHALL be to retry without re-preparing

#### Scenario: Base-moved failure kind requires a re-rebase

- **WHEN** `integrate:push` fails because the base branch moved after the rebase, producing a non-fast-forward push
- **THEN** the failure SHALL be reported with a base-moved kind
- **AND** the indicated next action SHALL be to rebase the branch again before pushing

#### Scenario: Conflict failure kind requires attention

- **WHEN** `integrate:rebase` fails because a conflict could not be resolved
- **THEN** the failure SHALL be reported with a conflict kind
- **AND** the indicated next action SHALL be that the conflict needs attention

#### Scenario: Branch-invariant violation failure kind is reported

- **WHEN** a delivery task observes the workflow workspace on a branch other than `workspace.branch`
- **THEN** the failure SHALL be reported with a branch-invariant-violation kind
- **AND** the failure SHALL be attributed to the runner or action rather than to issue work
- **AND** the failure SHALL be distinct from retry-safe, base-moved, and conflict kinds

### Requirement: Failed delivery leaves a clean workspace

A failed `integrate:rebase` or `integrate:push` SHALL leave the workflow workspace clean AND on its `workspace.branch` before the task reports failure: no in-progress rebase or merge SHALL remain, no conflict markers or partial resolution state SHALL be left behind, and the workspace SHALL be on `workspace.branch` rather than the base branch. Because both delivery tasks operate on the single workflow workspace without a landing clone, there SHALL be no secondary landing workspace to clean up.

#### Scenario: Failed rebase leaves no rebase in progress

- **WHEN** `integrate:rebase` fails
- **THEN** no in-progress rebase or merge SHALL remain in the workspace
- **AND** the working tree SHALL be clean of conflict markers and partial resolution state
- **AND** the workspace SHALL remain on `workspace.branch`

#### Scenario: Failed push leaves the workspace on the run branch

- **WHEN** `integrate:push` fails after attempting to push
- **THEN** the workflow workspace SHALL be left clean and on `workspace.branch` before the failure is reported
- **AND** the push task SHALL NOT have checked out the base branch or left the workspace in a landing-clone state
