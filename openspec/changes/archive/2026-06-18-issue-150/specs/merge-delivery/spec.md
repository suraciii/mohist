## MODIFIED Requirements

### Requirement: Prepare reconciles the issue branch with the base branch

The `integrate:prepare` task SHALL bring the run branch up to date with the latest base branch by rebasing the run branch onto the remote base ref, and SHALL resolve any conflicts that arise. Prepare SHALL stay on the run branch for its entire execution: it SHALL fetch the remote base ref, rebase `workspace.branch` onto `refs/remotes/<remote>/<baseBranch>`, and resolve conflicts in place, and it SHALL NOT check out the base branch inside the workflow workspace. This task SHALL be the single place where conflict resolution happens during delivery. The conflict-resolution work SHALL be attributable to the prepare task's own attempts and evidence so it can be seen and retried on its own. The prepare task SHALL NOT push to the remote.

#### Scenario: Clean branch prepares without conflict resolution

- **WHEN** the run branch can be brought up to date with the base branch without conflicts
- **THEN** `integrate:prepare` SHALL complete the rebase successfully while staying on `workspace.branch`
- **AND** the task result SHALL record the base commit it prepared against and the prepared candidate head
- **AND** the prepare task SHALL NOT check out the base branch inside the workflow workspace

#### Scenario: Conflicting branch resolves conflicts as visible prepare work

- **WHEN** bringing the run branch up to date with the base branch produces conflicts
- **THEN** `integrate:prepare` SHALL resolve the conflicts as part of the prepare task while staying on `workspace.branch`
- **AND** the conflict-resolution work SHALL be attributable to the prepare task's attempts and evidence
- **AND** no other delivery task SHALL perform conflict resolution

#### Scenario: Prepare never checks out the base branch

- **WHEN** `integrate:prepare` fetches, rebases, or resolves conflicts
- **THEN** the workspace SHALL remain on `workspace.branch` throughout
- **AND** prepare SHALL operate against the remote base ref without checking out the base branch inside the workflow workspace

### Requirement: Publish lands one commit and pushes to the remote

The `integrate:publish` task SHALL land the prepared issue changes as a single commit on the base branch and SHALL push that commit to the remote. Publish SHALL NOT check out the base branch inside the workflow workspace; the single landing commit SHALL be constructed through a branch-stable mechanism, such as an isolated temporary landing workspace or an equivalent ref-safe operation, so the workflow workspace remains on `workspace.branch` for the entire publish task. The publish task SHALL be the single owner for pushing to the remote; the prepare task SHALL NOT push. An issue's changes SHALL still land on the base branch as exactly one commit, preserving the existing user-visible delivery outcome.

#### Scenario: Publish lands a single commit and pushes

- **WHEN** `integrate:publish` runs after a successful prepare
- **THEN** the issue changes SHALL be landed on the base branch as a single commit
- **AND** that commit SHALL be pushed to the remote
- **AND** the task result SHALL record the landed commit and that the push occurred

#### Scenario: Publish lands without leaving the run branch

- **WHEN** `integrate:publish` constructs the landing commit and pushes to the remote
- **THEN** the landing commit SHALL be built in an isolated temporary landing workspace or equivalent ref-safe operation outside the workflow workspace
- **AND** the workflow workspace SHALL remain on `workspace.branch` for the entire publish task
- **AND** publish SHALL NOT run `git checkout <baseBranch>` inside the workflow workspace

#### Scenario: Publish re-attempts cheaply without conflict resolution

- **WHEN** the base branch has moved between `integrate:prepare` and `integrate:publish`
- **THEN** `integrate:publish` SHALL re-attempt landing cheaply without invoking conflict resolution
- **AND** it SHALL NOT silently repeat expensive conflict-resolution work in a loop

### Requirement: Delivery failures are classified into actionable kinds

A failed `integrate:prepare` or `integrate:publish` task SHALL report a failure kind that tells the user the nature of the problem and implies the next action. The delivery SHALL distinguish at least four failure kinds: a failure that is safe to retry as-is, a failure caused by the base branch having moved so the branch needs preparing again, a failure caused by a conflict that needs attention, and a branch-invariant violation caused by the workspace leaving the expected run branch. A branch-invariant violation SHALL be attributed to the runner or action, not to issue work.

#### Scenario: Retry-safe failure kind is reported

- **WHEN** a delivery task fails for a transient reason unrelated to base movement, conflicts, or branch stability
- **THEN** the failure SHALL be reported with a retry-safe kind
- **AND** the indicated user action SHALL be to retry without re-preparing

#### Scenario: Base-moved failure kind requires re-prepare

- **WHEN** `integrate:publish` fails because the base branch moved after prepare
- **THEN** the failure SHALL be reported with a base-moved kind
- **AND** the indicated next action SHALL be to prepare the branch again before publishing

#### Scenario: Conflict failure kind requires attention

- **WHEN** `integrate:prepare` fails because a conflict could not be resolved
- **THEN** the failure SHALL be reported with a conflict kind
- **AND** the indicated next action SHALL be that the conflict needs attention

#### Scenario: Branch-invariant violation failure kind is reported

- **WHEN** a delivery task observes the workflow workspace on a branch other than `workspace.branch`
- **THEN** the failure SHALL be reported with a branch-invariant-violation kind
- **AND** the failure SHALL be attributed to the runner or action rather than to issue work
- **AND** the failure SHALL be distinct from retry-safe, base-moved, and conflict kinds

### Requirement: Failed delivery leaves a clean workspace

A failed `integrate:prepare` or `integrate:publish` SHALL leave the workflow workspace clean AND on its `workspace.branch` before the task reports failure: no in-progress rebase or merge SHALL remain, no conflict markers or partial resolution state SHALL be left behind, any partially landed base branch SHALL be restored to its pre-attempt state, and the workspace SHALL be on `workspace.branch` rather than the base branch. Any isolated temporary landing workspace used by publish SHALL be cleaned up or left in an isolated location that does not affect the workflow workspace.

#### Scenario: Failed prepare leaves no rebase in progress

- **WHEN** `integrate:prepare` fails
- **THEN** no in-progress rebase or merge SHALL remain in the workspace
- **AND** the working tree SHALL be clean of conflict markers and partial resolution state
- **AND** the workspace SHALL remain on `workspace.branch`

#### Scenario: Failed publish rolls back the landing attempt and restores the run branch

- **WHEN** `integrate:publish` fails after partially landing or attempting to push
- **THEN** the base branch SHALL be restored to its pre-publish state
- **AND** the workflow workspace SHALL be left clean and on `workspace.branch` before the failure is reported
- **AND** any isolated temporary landing workspace SHALL NOT leave the workflow workspace on the base branch
