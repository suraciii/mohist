### Requirement: PR-first task graph

`mohist/pr` workflow profile SHALL express every GitHub PR side effect as an explicit task in the task graph. The profile SHALL create or update the PR via a `mohist/create-pull-request` task that runs after plan approval and before build. Any stage that needs to synchronize the working branch to GitHub SHALL declare an explicit `mohist/create-pull-request` (update PR) task at the tail of that stage's task list, reusing the same head and base. The integrate stage SHALL NOT contain a `mohist/create-pull-request` task on the happy path; its delivery task SHALL be exactly one `mohist/merge-pull-request`. The profile SHALL NOT introduce stage hooks, hidden stage-boundary side effects, or a workflow completion/finalize task to create, update, or merge the PR.

#### Scenario: PR created after plan approval

- **WHEN** a run using `mohist/pr` has its plan stage approved
- **THEN** the workflow SHALL execute a `mohist/create-pull-request` task that pushes the workflow branch and creates or updates the GitHub PR before any build-stage task runs

#### Scenario: Stage tail update PR task is explicit

- **WHEN** a stage needs to push its latest working-branch state to GitHub
- **THEN** the profile SHALL declare a `mohist/create-pull-request` task at the tail of that stage's task list
- **AND** no PR update SHALL occur as a hidden side effect of stage completion

#### Scenario: Integrate delivers via merge only

- **WHEN** the run reaches the integrate stage on the happy path
- **THEN** the integrate delivery SHALL consist of exactly one `mohist/merge-pull-request` task
- **AND** the integrate stage SHALL NOT contain a `mohist/create-pull-request` task on the happy path

#### Scenario: No hidden PR side effects

- **WHEN** the workflow engine transitions between stages or completes the run
- **THEN** no GitHub PR SHALL be created, updated, or merged except by an explicit task in the task graph

### Requirement: Stable PR identity projection

The `mohist/create-pull-request` action SHALL emit a stable PR identity in its action output. The profile SHALL project that identity into workflow runtime variables via `setVars` as `vars.github.pr.number` (the PR number) and `vars.github.pr.url` (the PR URL). Subsequent update-PR and merge-PR tasks SHALL consume the PR identity from these projected variables (or recover it from the same head/base). The projected identity SHALL be the single source of truth for which PR the run integrates through.

#### Scenario: PR identity written to runtime variables

- **WHEN** a `mohist/create-pull-request` task completes successfully
- **THEN** `vars.github.pr.number` SHALL equal the created or updated PR's number
- **AND** `vars.github.pr.url` SHALL equal the created or updated PR's URL

#### Scenario: Downstream tasks reuse projected identity

- **WHEN** a later update-PR or merge-PR task executes
- **THEN** it SHALL resolve the target PR from `vars.github.pr.number`
- **AND** it SHALL NOT open a second PR for the same head/base

### Requirement: Checks-gated merge

The `mohist/merge-pull-request` action SHALL treat GitHub PR checks as an internal precondition of merge, not as a stage-level check. Before invoking `gh pr merge`, the action SHALL wait for PR check status to resolve. While any required check is `pending`, the action SHALL continue waiting and SHALL NOT merge. When all considered checks are `passed` or `skipped`, the action SHALL perform a GitHub squash merge via `gh pr merge --squash`. When any considered check is `failed`, `cancelled`, or `action_required`, the action SHALL NOT merge and SHALL fail the task.

#### Scenario: Pending checks keep waiting

- **WHEN** `mohist/merge-pull-request` observes a PR check in `pending` state
- **THEN** the action SHALL keep waiting and SHALL NOT invoke `gh pr merge`

#### Scenario: Passed checks merge

- **WHEN** all considered PR checks are `passed` or `skipped`
- **THEN** the action SHALL execute `gh pr merge --squash` against the target PR

#### Scenario: Failed checks block merge

- **WHEN** any considered PR check is `failed`, `cancelled`, or `action_required`
- **THEN** the action SHALL NOT invoke `gh pr merge`
- **AND** the task SHALL fail

### Requirement: Merge confirmation

After `gh pr merge --squash` returns success, the `mohist/merge-pull-request` action SHALL re-query the PR and confirm its `state` is `MERGED`. The integrate stage SHALL be considered complete only after the PR `state` is confirmed `MERGED`. If the post-merge PR state is not `MERGED`, the action SHALL fail the task.

#### Scenario: Merge confirmed

- **WHEN** `gh pr merge --squash` returns success
- **THEN** the action SHALL re-query the PR state
- **AND** SHALL succeed only when the PR `state` is `MERGED`

#### Scenario: Merge not confirmed fails

- **WHEN** the post-merge PR `state` is not `MERGED`
- **THEN** the action SHALL fail the task

### Requirement: PR-checks-failed failure contract

When PR checks fail, block, or require action, the `mohist/merge-pull-request` action SHALL fail with an action-owned JSON output. The output SHALL include `errorCode: "pr-checks-failed"`, the `prNumber`, the `prUrl`, and a human-readable `message`. The `pr-checks-failed` error code SHALL NOT trigger any automatic fix recovery in the profile. The workflow SHALL preserve the failure as an ordinary task failure that the user can address and then retry or rerun.

#### Scenario: Failure output fields

- **WHEN** PR checks are `failed`, `cancelled`, or `action_required`
- **THEN** the failed task output SHALL be a JSON object containing `errorCode` equal to `pr-checks-failed`, `prNumber`, `prUrl`, and a non-empty `message`

#### Scenario: No auto-fix for pr-checks-failed

- **WHEN** a `mohist/merge-pull-request` task fails with `errorCode: pr-checks-failed`
- **THEN** the profile SHALL NOT insert any automatic recovery task for that error code
- **AND** the workflow SHALL surface the failure for user intervention

### Requirement: Base-moved recovery preserved

The existing `base-moved` recovery SHALL continue to work under the PR-first shape. When `mohist/merge-pull-request` fails with `errorCode: base-moved`, the profile SHALL insert recovery tasks that execute `mohist/rebase`, then `mohist/create-pull-request` (update PR), then `mohist/merge-pull-request`. Recovery SHALL reuse the same workflow branch and the same open PR; it SHALL NOT open a replacement PR.

#### Scenario: Base moved triggers rebase recovery

- **WHEN** `mohist/merge-pull-request` fails with `errorCode: base-moved`
- **THEN** the profile SHALL insert recovery tasks in order: `mohist/rebase`, `mohist/create-pull-request`, `mohist/merge-pull-request`

#### Scenario: Recovery reuses same branch and PR

- **WHEN** the `base-moved` recovery tasks execute
- **THEN** they SHALL push the same workflow branch and update the same open PR
- **AND** SHALL NOT create a new PR

### Requirement: PR checks are not stage-level checks

PR check status SHALL be evaluated only inside the `mohist/merge-pull-request` action as its internal merge precondition. Stage checks SHALL verify only the stage's own artifacts and SHALL NOT include any GitHub PR check. The workflow engine SHALL remain unaware of the meaning of `pr-checks-failed` or any other action-owned error code beyond generic JSON-path matching for recovery orchestration.

#### Scenario: Stage checks exclude PR checks

- **WHEN** a stage check runs in build or check stage
- **THEN** the check SHALL NOT query or assert GitHub PR check status

#### Scenario: Engine stays error-code-agnostic

- **WHEN** the workflow engine matches recovery cases
- **THEN** it SHALL perform generic JSON-path matching against action output
- **AND** SHALL NOT interpret the business meaning of `pr-checks-failed` or `base-moved`
