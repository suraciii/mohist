# GitHub PR Actions

GitHub PR Action repositories, branches, and Pull Request identities are
determined by explicit `with` inputs. An Action does not read implicit fallback
values from Variables and always uses the workspace supplied by the host.

In these examples, `${{ repository.gitUrl }}`,
`${{ repository.baseBranch }}`, and `${{ workspace.branch }}` come from the
repository and workspace for the current run. `${{ vars.github.pr.number }}`
is a Run Variable populated from the `prNumber` output of an earlier
`mohist/create-github-pr` Action. See
[Workflow Definition Reference](../workflow-definition.md#template-expressions)
for the complete expression and Variable rules.

## `mohist/create-github-pr`

Creates or updates a GitHub Pull Request for the current branch.

```yaml
- id: open-draft-pr
  uses: mohist/create-github-pr
  with:
    repositoryUrl: ${{ repository.gitUrl }}
    source: ${{ workspace.branch }}
    target: ${{ repository.baseBranch }}
    draft: true
    titleFrom: issue.title
    bodyFrom: issue.body
```

Inputs:

- `repositoryUrl` (required, text): Git repository URL that identifies the
  GitHub repository.
- `source` (required, text): source branch.
- `target` (required, text): target branch.
- `draft` (optional, Boolean, default `true`): whether to open the Pull
  Request as a draft.
- `title` (optional, text): explicit Pull Request title.
- `message` (optional, text): alias for `title`.
- `titleFrom` (optional, text, default `issue.title`): Issue field used as the
  Pull Request title.
- `body` (optional, text): explicit Pull Request body.
- `bodyFrom` (optional, text, default `issue.body`): Issue field used as the
  Pull Request body.

Outputs:

- `kind`: output type identifier.
- `status`: Pull Request status identifier.
- `source`: source branch.
- `targetBranch`: target branch.
- `branch`: head branch name.
- `prNumber`: Pull Request number.
- `prUrl`: Pull Request URL.
- `operation`: operation identifier: `created`, `updated`, or `reused`.
- `draft`: whether the Pull Request is a draft.
- `output`: aggregated `gh` output.
- `steps`: `gh` command results for each step.

Business error codes:

- `config-error`: GitHub configuration is missing or invalid.
- `protection-conflict`: branch protection rejected the Pull Request.
- `base-moved`: the base branch moved and the Pull Request is stale.
- `pr-state-conflict`: an existing Pull Request is in a conflicting state.
- `retry-safe`: the Pull Request operation can be retried safely.
- `create-pr-failed`: creating the Pull Request failed.

## `mohist/mark-github-pr-ready`

Marks the specified GitHub Pull Request ready for review. The operation is
idempotent when the Pull Request is already ready.

```yaml
- id: mark-pr-ready
  uses: mohist/mark-github-pr-ready
  with:
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
```

Inputs:

- `repositoryUrl` (required, text): Git repository URL that identifies the
  GitHub repository.
- `prNumber` (required, numeric): Pull Request number.

Outputs:

- `kind`: output type identifier.
- `status`: status identifier.
- `prNumber`: Pull Request number.
- `prUrl`: Pull Request URL.
- `state`: Pull Request state after the operation.
- `previousState`: Pull Request state before the operation.
- `transitioned`: whether the ready-state transition occurred.
- `output`: aggregated `gh` output.
- `steps`: `gh` command results for each step.

Business error codes:

- `config-error`: GitHub configuration is missing or invalid.
- `protection-conflict`: branch protection rejected the state transition.
- `base-moved`: the base branch moved and the Pull Request is stale.
- `pr-state-conflict`: an existing Pull Request is in a conflicting state.
- `retry-safe`: the operation can be retried safely.
- `mark-ready-failed`: marking the Pull Request ready failed.

## `mohist/merge-github-pr`

Squash-merges the specified GitHub Pull Request.

```yaml
- id: merge-pr
  uses: mohist/merge-github-pr
  with:
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
    method: squash
    subjectFrom: issue.title
```

Inputs:

- `repositoryUrl` (required, text): Git repository URL that identifies the
  GitHub repository.
- `method` (optional, text, default `squash`): merge method. Only `squash` is
  supported.
- `prNumber` (required, numeric): Pull Request number.
- `subject` (optional, text): explicit squash-commit subject.
- `subjectFrom` (optional, text, default `issue.title`): Issue field used as
  the squash-commit subject.

Outputs:

- `kind`: output type identifier.
- `status`: merge status identifier.
- `prNumber`: Pull Request number.
- `prUrl`: Pull Request URL.
- `mergeCommitSha`: SHA of the squash-merge commit.
- `method`: merge method that was used.
- `output`: aggregated `gh` output.
- `steps`: `gh` command results for each step.

Business error codes:

- `base-moved`: the base branch moved and the Pull Request is stale.
- `retry-safe`: the merge operation can be retried safely.
- `config-error`: GitHub configuration is missing or invalid.
- `protection-conflict`: branch protection rejected the merge.
- `pr-state-conflict`: an existing Pull Request is in a conflicting state.
- `pr-checks-unavailable`: Pull Request check status is unavailable.
- `pr-checks-failed`: required Pull Request checks did not pass.
- `merge-failed`: merging the Pull Request failed.

## `mohist/github-pr-status`

Verifies that the specified GitHub Pull Request is in the expected state.

```yaml
- id: verify-pr-status
  uses: mohist/github-pr-status
  with:
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
    expect: open,ready
```

Inputs:

- `repositoryUrl` (required, text): Git repository URL that identifies the
  GitHub repository.
- `prNumber` (required, numeric): Pull Request number.
- `expect` (optional, text, default `open,ready`): comma-separated expected
  states: `open`, `ready`, or `merged`.

Outputs:

- `kind`: output type identifier.
- `status`: status identifier.
- `prNumber`: Pull Request number.
- `prUrl`: Pull Request URL.
- `prState`: Pull Request state.
- `isDraft`: whether the Pull Request is a draft.
- `expectations`: expected-state markers.
- `missing`: expected-state markers that were not satisfied.
- `output`: aggregated `gh` output.
- `steps`: `gh` command results for each step.

Business error codes:

- `pr-status-failed`: Pull Request state validation failed.

## `mohist/github-pr-checks`

Waits for every check on the specified GitHub Pull Request to pass.

```yaml
- id: wait-for-pr-checks
  uses: mohist/github-pr-checks
  with:
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
```

Inputs:

- `repositoryUrl` (required, text): Git repository URL that identifies the
  GitHub repository.
- `prNumber` (required, numeric): Pull Request number.

Outputs:

- `kind`: output type identifier.
- `status`: check status identifier.
- `prNumber`: Pull Request number.
- `pollIntervalMs`: polling interval in milliseconds.
- `message`: user-facing check result.
- `output`: aggregated `gh` output.
- `steps`: `gh` command results for each step.

Business error codes:

- `config-error`: GitHub configuration is missing or invalid.
- `pr-checks-unavailable`: Pull Request check status is unavailable.
- `pr-checks-failed`: required Pull Request checks did not pass.
- `aborted`: polling was cancelled.
