## ADDED Requirements

### Requirement: mohist/publish-via-pr is the PR-based integrate delivery action

The runner SHALL provide a `mohist/publish-via-pr` action that delivers an issue's prepared changes to the base branch through a GitHub pull request. The action SHALL execute three ordered steps in a single task invocation: force-with-lease push, open-or-reuse PR, merge PR. The action SHALL NOT perform local squash; the single-commit-on-base invariant SHALL be produced by GitHub's squash merge at PR merge time. The action SHALL be the single owner of remote writes for PR-based delivery and SHALL NOT be combined with `mohist/publish` in the same workflow.

#### Scenario: Action performs three ordered steps

- **WHEN** `integrate:publish` runs the `mohist/publish-via-pr` action
- **THEN** the action SHALL first push `workspace.branch` to `origin` with `--force-with-lease`
- **AND** it SHALL then open a new PR or reuse the existing open PR for the same `head:base` pair
- **AND** it SHALL finally merge the PR via GitHub's squash merge unless the PR is already merged
- **AND** it SHALL NOT execute any of these steps out of order

#### Scenario: Force-with-lease push is idempotent

- **WHEN** the action re-executes after a previous attempt pushed the same branch
- **THEN** the `--force-with-lease` push SHALL overwrite the runner's own previous push to the same branch
- **AND** it SHALL NOT overwrite remote commits authored outside the runner's prior attempts
- **AND** a divergent remote that is not the runner's own previous push SHALL fail with `retry-safe` or `pr-state-conflict`

### Requirement: mohist/publish-via-pr is idempotent across retries and runner-lost recovery

Every step of `mohist/publish-via-pr` SHALL be safe to re-execute. A retry after partial failure, or a recovery invocation after runner-lost, SHALL converge to the same end state as a single successful run without side effects beyond the intended PR and merge.

#### Scenario: Existing open PR is reused

- **WHEN** an open PR already exists for the same `head:base` pair
- **THEN** the action SHALL reuse that PR
- **AND** it SHALL NOT create a duplicate PR
- **AND** it SHALL NOT close or recreate the existing PR

#### Scenario: Existing branch push does not fail re-attempt

- **WHEN** a previous attempt already pushed `workspace.branch` to `origin`
- **THEN** a re-attempt SHALL succeed rather than fail with a non-fast-forward error
- **AND** it SHALL NOT require manual remote cleanup to proceed

#### Scenario: Already-merged PR is reported as success

- **WHEN** the action observes the candidate PR in `state=merged` before invoking merge
- **THEN** the action SHALL report success without invoking `gh pr merge`
- **AND** it SHALL return the recorded `prNumber`, `prUrl`, and `mergeCommitSha` from the merged PR
- **AND** it SHALL NOT raise a "PR already merged" error

### Requirement: gh CLI is a fail-fast prerequisite for PR-based delivery

The runner host SHALL have the GitHub CLI (`gh`) installed and authenticated via `gh auth login` before `mohist/publish-via-pr` is dispatched. The action SHALL verify `gh` availability and authentication as its first operation and SHALL fail fast with kind `config-error` when the prerequisite is missing. The action SHALL NOT attempt to install `gh`, perform interactive login, or store GitHub tokens. Mohist SHALL NOT persist GitHub credentials.

#### Scenario: Missing gh CLI fails fast

- **WHEN** `mohist/publish-via-pr` is dispatched on a host where `gh` is not on PATH
- **THEN** the action SHALL fail with kind `config-error`
- **AND** the failure message SHALL instruct the operator to install `gh` and run `gh auth login`
- **AND** the action SHALL NOT retry the missing-CLI condition

#### Scenario: Unauthenticated gh CLI fails fast

- **WHEN** `gh` is installed but `gh auth login` has not been completed
- **THEN** the action SHALL fail with kind `config-error`
- **AND** the failure message SHALL instruct the operator to run `gh auth login`
- **AND** the action SHALL NOT attempt to authenticate on behalf of the operator

#### Scenario: Mohist does not own GitHub credentials

- **WHEN** `mohist/publish-via-pr` performs any PR operation
- **THEN** the action SHALL rely exclusively on `gh`'s host-level authentication
- **AND** Mohist SHALL NOT read, store, or transmit a GitHub token

### Requirement: PR delivery failures are classified into actionable kinds

A failed `mohist/publish-via-pr` task SHALL report a failure kind that implies the next action. The action SHALL distinguish at least five failure kinds: `base-moved`, `retry-safe`, `config-error`, `protection-conflict`, and `pr-state-conflict`. Kinds that require human intervention or environment fix SHALL NOT trigger automatic workflow retry. The action SHALL NOT perform an internal rebase loop; `base-moved` SHALL converge via workflow-level integrate retry that re-runs fetch, rebase, and force-push before re-attempting merge.

#### Scenario: base-moved is retry-converging

- **WHEN** the PR is unmergeable because the base branch moved after `integrate:prepare`
- **THEN** the failure SHALL be reported with kind `base-moved`
- **AND** the workflow integrate retry SHALL re-run fetch, rebase, and force-push before re-attempting merge
- **AND** the action SHALL NOT perform its own rebase loop internally

#### Scenario: Transient network or rate-limit failure is retry-safe

- **WHEN** a `gh` invocation fails for a transient network or rate-limit reason
- **THEN** the failure SHALL be reported with kind `retry-safe`
- **AND** the indicated action SHALL be to retry after backoff without re-preparing

#### Scenario: gh misconfiguration is config-error

- **WHEN** `gh` is missing or unauthenticated
- **THEN** the failure SHALL be reported with kind `config-error`
- **AND** the workflow SHALL NOT retry the task automatically
- **AND** the failure SHALL surface to a human operator

#### Scenario: Branch protection conflict is protection-conflict

- **WHEN** GitHub rejects the merge because branch protection requires status checks or reviews that Mohist does not satisfy
- **THEN** the failure SHALL be reported with kind `protection-conflict`
- **AND** the workflow SHALL NOT retry the task automatically
- **AND** the failure SHALL surface to a human operator as a configuration conflict

#### Scenario: External PR state change is pr-state-conflict

- **WHEN** the PR was closed or its state was changed externally between action steps
- **THEN** the failure SHALL be reported with kind `pr-state-conflict`
- **AND** the workflow SHALL NOT retry the task automatically
- **AND** the failure SHALL surface to a human operator

### Requirement: PR metadata follows fixed conventions

The PR title, body, and squash merge commit message SHALL follow fixed conventions so the GitHub integration record matches the existing direct-delivery commit message and stays consistent across issues. The action SHALL NOT emit `Closes #N` or any GitHub issue-closing directive in the PR title or body, because issue lifecycle ownership stays with Mohist.

#### Scenario: PR title and body follow issue-scoped conventions

- **WHEN** `mohist/publish-via-pr` opens a new PR for issue `N`
- **THEN** the PR title SHALL be `Complete issue #N`
- **AND** the PR body SHALL be the literal text `Mohist issue #N`
- **AND** the PR body SHALL NOT contain `Closes #N`, `Fixes #N`, or any GitHub issue-closing keyword

#### Scenario: Squash merge commit message matches direct delivery

- **WHEN** `mohist/publish-via-pr` merges the PR via GitHub's squash merge
- **THEN** the squash merge commit on the base branch SHALL use subject `Complete issue #N`
- **AND** the subject SHALL be controlled via `gh pr merge --subject`
- **AND** the resulting commit message SHALL match the `mohist/default` direct-push commit message for the same issue

#### Scenario: PR preserves the full AI commit history

- **WHEN** the action force-pushes `workspace.branch` to `origin`
- **THEN** the pushed branch SHALL retain the full set of AI intermediate commits produced during the workflow
- **AND** the PR SHALL display that full commit history on GitHub
- **AND** the action SHALL NOT squash or rewrite those commits locally before push

### Requirement: mohist/publish-via-pr records PR delivery metadata on the task result

On successful PR merge, the action SHALL record `prNumber`, `prUrl`, and `mergeCommitSha` on the publish task's structured output. The action SHALL confirm the PR is in `state=merged` before reporting success. These outputs SHALL be readable through the existing WorkflowRun task-result read model without a new schema.

#### Scenario: Successful merge records PR identifiers

- **WHEN** `mohist/publish-via-pr` completes after merging the PR
- **THEN** the task result SHALL include `prNumber` as the GitHub PR number
- **AND** it SHALL include `prUrl` as the GitHub PR URL
- **AND** it SHALL include `mergeCommitSha` as the sha of the commit GitHub created on the base branch
- **AND** it SHALL confirm the PR `state=merged` before reporting success

#### Scenario: PR delivery completion marks workflow done

- **WHEN** `mohist/publish-via-pr` reports success with `state=merged`
- **THEN** the workflow SHALL treat the issue as delivered
- **AND** the workflow SHALL NOT require a separate completion signal beyond the publish task result

### Requirement: PR delivery leaves the workspace on the run branch

`mohist/publish-via-pr` SHALL leave the workflow workspace on `workspace.branch` and clean of conflict markers whether it succeeds or fails. The action SHALL NOT check out the base branch inside the workflow workspace. The action SHALL NOT delete remote feature branches; remote head-branch cleanup SHALL rely on the GitHub repository's "Automatically delete head branches" setting.

#### Scenario: Successful delivery leaves the workspace on the run branch

- **WHEN** `mohist/publish-via-pr` completes successfully
- **THEN** the workflow workspace SHALL remain on `workspace.branch`
- **AND** the working tree SHALL be clean of conflict markers
- **AND** the action SHALL NOT have checked out the base branch inside the workflow workspace

#### Scenario: Failed delivery leaves a clean recoverable workspace

- **WHEN** `mohist/publish-via-pr` fails at any step
- **THEN** the workflow workspace SHALL remain on `workspace.branch`
- **AND** no partial merge or push state SHALL be left in the workspace
- **AND** the failure SHALL be recoverable by workflow-level integrate retry

#### Scenario: Remote head-branch deletion is not performed by Mohist

- **WHEN** a PR is merged successfully
- **THEN** the action SHALL NOT delete the remote feature branch
- **AND** remote head-branch cleanup SHALL be delegated to the GitHub repository auto-delete setting
- **AND** the action SHALL NOT emit any `gh api` or `git push origin --delete` call to remove the head branch
