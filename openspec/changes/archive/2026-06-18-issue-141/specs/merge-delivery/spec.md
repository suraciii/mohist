## ADDED Requirements

### Requirement: Integrate delivery is prepare then publish

The Integrate delivery SHALL be executed as two ordered, independently visible tasks: `integrate:prepare` followed by `integrate:publish`. Each task SHALL be a genuine, independently tracked unit of work that appears in the task list with its own title, status, attempts, and result evidence. The delivery SHALL NOT be performed by a single opaque merge task, and conflict resolution SHALL NOT run hidden inside another task.

#### Scenario: Delivery appears as two visible tasks

- **WHEN** an issue reaches the delivery portion of Integrate
- **THEN** the task list SHALL show `integrate:prepare` and `integrate:publish` as separate ordered tasks
- **AND** each task SHALL carry its own title, status, attempts, and result evidence

#### Scenario: Publish runs only after prepare succeeds

- **WHEN** `integrate:prepare` has not reached a successful terminal state
- **THEN** `integrate:publish` SHALL NOT execute
- **AND** a failed prepare SHALL block publish through ordinary task-failure semantics

### Requirement: Prepare reconciles the issue branch with the base branch

The `integrate:prepare` task SHALL bring the issue branch up to date with the latest base branch by rebasing the issue branch onto the base branch, and SHALL resolve any conflicts that arise. This task SHALL be the single place where conflict resolution happens during delivery. The conflict-resolution work SHALL be attributable to the prepare task's own attempts and evidence so it can be seen and retried on its own. The prepare task SHALL NOT push to the remote.

#### Scenario: Clean branch prepares without conflict resolution

- **WHEN** the issue branch can be brought up to date with the base branch without conflicts
- **THEN** `integrate:prepare` SHALL complete the rebase successfully
- **AND** the task result SHALL record the base commit it prepared against and the prepared candidate head

#### Scenario: Conflicting branch resolves conflicts as visible prepare work

- **WHEN** bringing the issue branch up to date with the base branch produces conflicts
- **THEN** `integrate:prepare` SHALL resolve the conflicts as part of the prepare task
- **AND** the conflict-resolution work SHALL be attributable to the prepare task's attempts and evidence
- **AND** no other delivery task SHALL perform conflict resolution

### Requirement: Publish lands one commit and pushes to the remote

The `integrate:publish` task SHALL land the prepared issue changes as a single commit on the base branch and SHALL push that commit to the remote. The publish task SHALL be the single owner for pushing to the remote; the prepare task SHALL NOT push. An issue's changes SHALL still land on the base branch as exactly one commit, preserving the existing user-visible delivery outcome.

#### Scenario: Publish lands a single commit and pushes

- **WHEN** `integrate:publish` runs after a successful prepare
- **THEN** the issue changes SHALL be landed on the base branch as a single commit
- **AND** that commit SHALL be pushed to the remote
- **AND** the task result SHALL record the landed commit and that the push occurred

#### Scenario: Publish re-attempts cheaply without conflict resolution

- **WHEN** the base branch has moved between `integrate:prepare` and `integrate:publish`
- **THEN** `integrate:publish` SHALL re-attempt landing cheaply without invoking conflict resolution
- **AND** it SHALL NOT silently repeat expensive conflict-resolution work in a loop

### Requirement: Delivery failures are classified into actionable kinds

A failed `integrate:prepare` or `integrate:publish` task SHALL report a failure kind that tells the user the nature of the problem and implies the next action. The delivery SHALL distinguish at least three failure kinds: a failure that is safe to retry as-is, a failure caused by the base branch having moved so the branch needs preparing again, and a failure caused by a conflict that needs attention.

#### Scenario: Retry-safe failure kind is reported

- **WHEN** a delivery task fails for a transient reason unrelated to base movement or conflicts
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

### Requirement: Failed delivery leaves a clean workspace

A failed `integrate:prepare` or `integrate:publish` SHALL leave the workspace clean before the task reports failure: no in-progress rebase or merge SHALL remain, no conflict markers or partial resolution state SHALL be left behind, and any partially landed base branch SHALL be restored to its pre-attempt state.

#### Scenario: Failed prepare leaves no rebase in progress

- **WHEN** `integrate:prepare` fails
- **THEN** no in-progress rebase or merge SHALL remain in the workspace
- **AND** the working tree SHALL be clean of conflict markers and partial resolution state

#### Scenario: Failed publish rolls back the landing attempt

- **WHEN** `integrate:publish` fails after partially landing or attempting to push
- **THEN** the base branch SHALL be restored to its pre-publish state
- **AND** the workspace SHALL be left clean before the failure is reported
