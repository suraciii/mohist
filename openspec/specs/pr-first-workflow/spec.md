

### Requirement: PR-first task graph

The `mohist/github-pr` workflow profile SHALL express every GitHub PR side effect as an explicit task in the task graph. The profile SHALL create or reuse a draft GitHub PR via a `mohist/create-github-pr` task (`open-draft-pr`) that runs as the last task of the plan stage, after `self-review`, once all plan artifacts are ready; the task SHALL push the workflow branch and create the PR as a draft. The profile SHALL NOT introduce a `ready` stage, stage hooks, hidden stage-boundary side effects, or a workflow completion/finalize task to create, update, or merge the PR. Synchronizing the working branch to GitHub SHALL be expressed only through explicit `mohist/push` tasks. Marking a PR ready for review SHALL be expressed only through explicit `mohist/mark-github-pr-ready` tasks. The integrate stage delivery task SHALL be exactly one `mohist/merge-github-pr`; the integrate stage SHALL NOT contain a `mohist/create-github-pr` task on the happy path.

#### Scenario: Draft PR opened as last plan task

- **WHEN** a run using `mohist/github-pr` completes the prior plan-stage tasks
- **THEN** the workflow SHALL execute `open-draft-pr` (`mohist/create-github-pr`) as the final task of the plan stage
- **AND** the task SHALL create or reuse the draft GitHub PR after `self-review` has produced its artifact

#### Scenario: Branch sync is explicit

- **WHEN** a stage needs to synchronize the working branch to GitHub
- **THEN** the profile SHALL declare a `mohist/push` task at the point synchronization is required
- **AND** no branch push SHALL occur as a hidden side effect of stage or task completion

#### Scenario: PR readiness is explicit

- **WHEN** a stage needs to mark the PR ready for review
- **THEN** the profile SHALL declare a `mohist/mark-github-pr-ready` task
- **AND** no ready transition SHALL occur as a hidden side effect

#### Scenario: Integrate delivers via merge only

- **WHEN** the run reaches the integrate stage on the happy path
- **THEN** the integrate delivery SHALL consist of exactly one `mohist/merge-github-pr` task
- **AND** the integrate stage SHALL NOT contain a `mohist/create-github-pr` task on the happy path

#### Scenario: No hidden PR side effects

- **WHEN** the workflow engine transitions between stages or completes the run
- **THEN** no GitHub PR SHALL be created, updated, marked ready, or merged except by an explicit task in the task graph

### Requirement: Stable PR identity projection

The `mohist/create-github-pr` action SHALL emit a stable PR identity in its action output. The profile SHALL project that identity into workflow runtime variables via `setVars` as `vars.github.pr.number` (the PR number) and `vars.github.pr.url` (the PR URL). Subsequent `mark-github-pr-ready`, `push`, and `merge-github-pr` tasks SHALL consume the PR identity from these projected variables (or recover it from the same head/base). The projected identity SHALL be the single source of truth for which PR the run integrates through.

#### Scenario: PR identity written to runtime variables

- **WHEN** a `mohist/create-github-pr` task completes successfully
- **THEN** `vars.github.pr.number` SHALL equal the created or reused PR's number
- **AND** `vars.github.pr.url` SHALL equal the created or reused PR's URL

#### Scenario: Downstream tasks reuse projected identity

- **WHEN** a later `mark-github-pr-ready`, `push`, or `merge-github-pr` task executes
- **THEN** it SHALL resolve the target PR from `vars.github.pr.number`
- **AND** SHALL NOT open a second PR for the same head/base

### Requirement: Checks-gated merge

The `mohist/merge-github-pr` action SHALL treat GitHub PR checks as an internal precondition of merge, not as a stage-level check. Before invoking `gh pr merge`, the action SHALL wait for PR check status to resolve. While any considered check is `pending`, the action SHALL continue waiting and SHALL NOT merge. When all considered checks are `passed` or `skipped`, the action SHALL perform a GitHub squash merge via `gh pr merge --squash`. When any considered check is `failed`, `cancelled`, or `action_required`, the action SHALL NOT merge and SHALL fail the task.

#### Scenario: Pending checks keep waiting

- **WHEN** `mohist/merge-github-pr` observes a PR check in `pending` state
- **THEN** the action SHALL keep waiting and SHALL NOT invoke `gh pr merge`

#### Scenario: Passed checks merge

- **WHEN** all considered PR checks are `passed` or `skipped`
- **THEN** the action SHALL execute `gh pr merge --squash` against the target PR

#### Scenario: Failed checks block merge

- **WHEN** any considered PR check is `failed`, `cancelled`, or `action_required`
- **THEN** the action SHALL NOT invoke `gh pr merge`
- **AND** the task SHALL fail

### Requirement: Merge confirmation

After `gh pr merge --squash` returns success, the `mohist/merge-github-pr` action SHALL re-query the PR and confirm its `state` is `MERGED`. The integrate stage SHALL be considered complete only after the PR `state` is confirmed `MERGED`. If the post-merge PR state is not `MERGED`, the action SHALL fail the task.

#### Scenario: Merge confirmed

- **WHEN** `gh pr merge --squash` returns success
- **THEN** the action SHALL re-query the PR state
- **AND** SHALL succeed only when the PR `state` is `MERGED`

#### Scenario: Merge not confirmed fails

- **WHEN** the post-merge PR `state` is not `MERGED`
- **THEN** the action SHALL fail the task

### Requirement: PR-checks-failed failure contract

When PR checks fail, are cancelled, or require action, the `mohist/merge-github-pr` action SHALL fail with an action-owned JSON output. The output SHALL include `errorCode: "pr-checks-failed"`, the `prNumber`, the `prUrl`, and a human-readable `message`. The `mohist/github-pr` profile SHALL declare an explicit `onFailure` recovery case for `errorCode: pr-checks-failed` consisting of a `recover:fix-pr-checks` agent task followed by a `recover:push` task and `retry: self` of the original `merge-github-pr` task. The `pr-checks-failed` error code SHALL NOT trigger any recovery outside of this declared case.

#### Scenario: Failure output fields

- **WHEN** PR checks are `failed`, `cancelled`, or `action_required`
- **THEN** the failed task output SHALL be a JSON object containing `errorCode` equal to `pr-checks-failed`, `prNumber`, `prUrl`, and a non-empty `message`

#### Scenario: Declared recovery fixes and retries

- **WHEN** a `mohist/merge-github-pr` task fails with `errorCode: pr-checks-failed`
- **THEN** the profile SHALL insert `recover:fix-pr-checks` followed by `recover:push`
- **AND** SHALL append a fresh attempt of the original `merge-github-pr` task via `retry: self`

#### Scenario: No recovery outside the declared case

- **WHEN** a `mohist/merge-github-pr` task fails with `errorCode: pr-checks-failed`
- **THEN** no recovery SHALL occur except the `recover:fix-pr-checks`, `recover:push`, and `retry: self` sequence declared by the profile

### Requirement: Base-moved recovery preserved

When `mohist/merge-github-pr` fails with `errorCode: base-moved`, the profile SHALL insert recovery tasks that execute `mohist/rebase` (`recover:rebase`), then `mohist/push` (`recover:push`), and then `retry: self` of the original `merge-github-pr` task. Recovery SHALL reuse the same workflow branch and the same open PR; it SHALL NOT open a replacement PR and SHALL NOT re-mark the PR ready. When `mohist/rebase` is declared with `conflictMode: task` and conflicts occur, it SHALL return `output.failureKind: conflict` and SHALL leave the rebase in progress; the profile SHALL then resolve conflicts via an explicit `recover:resolve-rebase-conflicts` agent task declared under `recover:rebase.onFailure`, after which the workflow SHALL continue to `recover:push` and then retry the merge.

#### Scenario: Base moved triggers rebase recovery

- **WHEN** `mohist/merge-github-pr` fails with `errorCode: base-moved`
- **THEN** the profile SHALL insert recovery tasks in order: `recover:rebase`, `recover:push`
- **AND** SHALL append a fresh attempt of the original `merge-github-pr` task via `retry: self`

#### Scenario: Recovery reuses same branch and PR

- **WHEN** the `base-moved` recovery tasks execute
- **THEN** they SHALL push the same workflow branch and update the same open PR
- **AND** SHALL NOT create a new PR
- **AND** SHALL NOT re-mark the PR ready

#### Scenario: Rebase conflict delegates to resolution task

- **WHEN** `recover:rebase` runs with `conflictMode: task` and a conflict occurs
- **THEN** `mohist/rebase` SHALL return `output.failureKind: conflict` and SHALL leave the rebase in progress
- **AND** the profile SHALL execute `recover:resolve-rebase-conflicts` declared under `recover:rebase.onFailure`
- **AND** after that task completes successfully the workflow SHALL continue to `recover:push` and then retry the merge

### Requirement: PR checks are not stage-level checks

PR check status SHALL be evaluated only inside the `mohist/merge-github-pr` action as its internal merge precondition. Stage checks SHALL verify only the stage's own artifacts and SHALL NOT include any GitHub PR check. The check stage of `mohist/github-pr` SHALL declare exactly one stage check, the read-only `mohist/github-pr-status` check; it SHALL NOT declare `health`, `review-passed`, or `merge-ready` checks. The integrate stage check SHALL be `merge-verified` using `mohist/github-pr-status` with `expect: merged`. The workflow engine SHALL remain unaware of the meaning of `pr-checks-failed` or any other action-owned error code beyond generic JSON-path matching for recovery orchestration.

#### Scenario: Stage checks exclude PR checks

- **WHEN** a stage check runs in the check stage
- **THEN** the check SHALL NOT query or assert GitHub PR check status
- **AND** the check stage SHALL declare exactly the read-only `mohist/github-pr-status` check

#### Scenario: Integrate verifies merge

- **WHEN** the integrate stage runs its stage check
- **THEN** it SHALL be `merge-verified` using `mohist/github-pr-status` with `expect: merged`

#### Scenario: Engine stays error-code-agnostic

- **WHEN** the workflow engine matches recovery cases
- **THEN** it SHALL perform generic JSON-path matching against action output
- **AND** SHALL NOT interpret the business meaning of `pr-checks-failed` or `base-moved`
