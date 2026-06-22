## MODIFIED Requirements

### Requirement: Publish lands one commit and pushes to the remote

The `integrate:publish` task SHALL land the prepared issue changes as a single commit on the base branch and SHALL make that commit reachable from the remote base branch. The publish task SHALL support two delivery shapes selected by the workflow profile: the **direct** shape used by `mohist/default` constructs the single landing commit locally via squash and fast-forward pushes it; the **PR-based** shape used by `mohist/pr` force-pushes the prepared branch, opens or reuses a GitHub PR for `head:base`, and merges it via GitHub's squash merge. In both shapes the publish task SHALL NOT check out the base branch inside the workflow workspace; the single landing commit SHALL be constructed through a branch-stable mechanism — an isolated temporary landing workspace for the direct shape, or GitHub's squash merge for the PR-based shape — so the workflow workspace remains on `workspace.branch` for the entire publish task. The publish task SHALL be the single owner for remote writes; the prepare task SHALL NOT push. An issue's changes SHALL land on the base branch as exactly one commit under both delivery shapes, preserving the user-visible delivery outcome.

#### Scenario: Direct publish lands a single commit and pushes

- **WHEN** `integrate:publish` runs the direct shape after a successful prepare
- **THEN** the issue changes SHALL be landed on the base branch as a single commit constructed locally via squash
- **AND** that commit SHALL be fast-forward pushed to the remote
- **AND** the task result SHALL record the landed commit and that the push occurred

#### Scenario: PR-based publish lands a single commit via GitHub squash merge

- **WHEN** `integrate:publish` runs the PR-based shape after a successful prepare
- **THEN** the action SHALL force-push `workspace.branch` to `origin` without local squash
- **AND** it SHALL open or reuse the open PR for the same `head:base` pair
- **AND** it SHALL merge the PR via GitHub's squash merge so exactly one commit lands on the base branch
- **AND** the task result SHALL record `prNumber`, `prUrl`, and `mergeCommitSha`

#### Scenario: Publish lands without leaving the run branch

- **WHEN** `integrate:publish` constructs the landing commit and pushes to the remote
- **THEN** the landing commit SHALL be built in an isolated temporary landing workspace (direct shape) or by GitHub's squash merge (PR-based shape), both outside the workflow workspace
- **AND** the workflow workspace SHALL remain on `workspace.branch` for the entire publish task
- **AND** publish SHALL NOT run `git checkout <baseBranch>` inside the workflow workspace

#### Scenario: Publish re-attempts cheaply without conflict resolution

- **WHEN** the base branch has moved between `integrate:prepare` and `integrate:publish`
- **THEN** `integrate:publish` SHALL re-attempt landing cheaply without invoking conflict resolution
- **AND** it SHALL NOT silently repeat expensive conflict-resolution work in a loop
- **AND** under the PR-based shape a base-moved failure SHALL converge via workflow-level integrate retry rather than an action-internal rebase loop

### Requirement: Delivery failures are classified into actionable kinds

A failed `integrate:prepare` or `integrate:publish` task SHALL report a failure kind that tells the user the nature of the problem and implies the next action. The delivery SHALL distinguish at least four failure kinds for the direct shape: a failure that is safe to retry as-is (`retry-safe`), a failure caused by the base branch having moved so the branch needs preparing again (`base-moved`), a failure caused by a conflict that needs attention (`conflict`), and a branch-invariant violation caused by the workspace leaving the expected run branch (`branch-invariant-violation`). The PR-based shape SHALL additionally distinguish `config-error` (gh CLI missing or unauthenticated), `protection-conflict` (GitHub branch protection blocks the merge), and `pr-state-conflict` (PR closed or externally state-changed). A branch-invariant violation SHALL be attributed to the runner or action, not to issue work. Kinds that require human intervention or environment fix (`config-error`, `protection-conflict`, `pr-state-conflict`) SHALL NOT trigger automatic workflow retry.

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

#### Scenario: PR shape config-error is not retried

- **WHEN** `mohist/publish-via-pr` reports a `config-error` failure
- **THEN** the workflow SHALL NOT retry the task automatically
- **AND** the failure SHALL surface to a human operator as an environment problem

#### Scenario: PR shape protection-conflict is not retried

- **WHEN** `mohist/publish-via-pr` reports a `protection-conflict` failure
- **THEN** the workflow SHALL NOT retry the task automatically
- **AND** the failure SHALL surface to a human operator as a configuration conflict

#### Scenario: PR shape pr-state-conflict is not retried

- **WHEN** `mohist/publish-via-pr` reports a `pr-state-conflict` failure
- **THEN** the workflow SHALL NOT retry the task automatically
- **AND** the failure SHALL surface to a human operator as an external state change
