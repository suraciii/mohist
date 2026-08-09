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

### Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `repositoryUrl` | Yes | - | Git repository URL that identifies the GitHub repository. The value is text. |
| `source` | Yes | - | Source branch. The value is text. |
| `target` | Yes | - | Target branch. The value is text. |
| `draft` | No | `true` | Whether to open the Pull Request as a draft. The value is Boolean. |
| `title` | No | - | Explicit Pull Request title. The value is text. |
| `message` | No | - | Alias for `title`. The value is text. |
| `titleFrom` | No | `issue.title` | Issue field used as the Pull Request title. The value is text. |
| `body` | No | - | Explicit Pull Request body. The value is text. |
| `bodyFrom` | No | `issue.body` | Issue field used as the Pull Request body. The value is text. |

### Outputs

| Field | Meaning |
|---|---|
| `kind` | Output type identifier. |
| `status` | Pull Request status identifier. |
| `source` | Source branch. |
| `targetBranch` | Target branch. |
| `branch` | Head branch name. |
| `prNumber` | Pull Request number. |
| `prUrl` | Pull Request URL. |
| `operation` | Operation identifier: `created`, `updated`, or `reused`. |
| `draft` | Whether the Pull Request is a draft. |
| `output` | Aggregated `gh` output. |
| `steps` | `gh` command results for each step. |

### Business Error Codes

| Error code | Meaning |
|---|---|
| `config-error` | GitHub configuration is missing or invalid. |
| `protection-conflict` | Branch protection rejected the Pull Request. |
| `base-moved` | The base branch moved and the Pull Request is stale. |
| `pr-state-conflict` | An existing Pull Request is in a conflicting state. |
| `retry-safe` | The Pull Request operation can be retried safely. |
| `create-pr-failed` | Creating the Pull Request failed. |

### Example

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

## `mohist/mark-github-pr-ready`

Marks the specified GitHub Pull Request ready for review. The operation is
idempotent when the Pull Request is already ready.

### Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `repositoryUrl` | Yes | - | Git repository URL that identifies the GitHub repository. The value is text. |
| `prNumber` | Yes | - | Pull Request number. The value is numeric. |

### Outputs

| Field | Meaning |
|---|---|
| `kind` | Output type identifier. |
| `status` | Status identifier. |
| `prNumber` | Pull Request number. |
| `prUrl` | Pull Request URL. |
| `state` | Pull Request state after the operation. |
| `previousState` | Pull Request state before the operation. |
| `transitioned` | Whether the ready-state transition occurred. |
| `output` | Aggregated `gh` output. |
| `steps` | `gh` command results for each step. |

### Business Error Codes

| Error code | Meaning |
|---|---|
| `config-error` | GitHub configuration is missing or invalid. |
| `protection-conflict` | Branch protection rejected the state transition. |
| `base-moved` | The base branch moved and the Pull Request is stale. |
| `pr-state-conflict` | An existing Pull Request is in a conflicting state. |
| `retry-safe` | The operation can be retried safely. |
| `mark-ready-failed` | Marking the Pull Request ready failed. |

### Example

```yaml
- id: mark-pr-ready
  uses: mohist/mark-github-pr-ready
  with:
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
```

## `mohist/merge-github-pr`

Squash-merges the specified GitHub Pull Request.

### Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `repositoryUrl` | Yes | - | Git repository URL that identifies the GitHub repository. The value is text. |
| `method` | No | `squash` | Merge method. Only `squash` is supported. The value is text. |
| `prNumber` | Yes | - | Pull Request number. The value is numeric. |
| `subject` | No | - | Explicit squash-commit subject. The value is text. |
| `subjectFrom` | No | `issue.title` | Issue field used as the squash-commit subject. The value is text. |

### Outputs

| Field | Meaning |
|---|---|
| `kind` | Output type identifier. |
| `status` | Merge status identifier. |
| `prNumber` | Pull Request number. |
| `prUrl` | Pull Request URL. |
| `mergeCommitSha` | SHA of the squash-merge commit. |
| `method` | Merge method that was used. |
| `output` | Aggregated `gh` output. |
| `steps` | `gh` command results for each step. |

### Business Error Codes

| Error code | Meaning |
|---|---|
| `base-moved` | The base branch moved and the Pull Request is stale. |
| `retry-safe` | The merge operation can be retried safely. |
| `config-error` | GitHub configuration is missing or invalid. |
| `protection-conflict` | Branch protection rejected the merge. |
| `pr-state-conflict` | An existing Pull Request is in a conflicting state. |
| `pr-checks-unavailable` | Pull Request check status is unavailable. |
| `pr-checks-failed` | Required Pull Request checks did not pass. |
| `merge-failed` | Merging the Pull Request failed. |

### Example

```yaml
- id: merge-pr
  uses: mohist/merge-github-pr
  with:
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
    method: squash
    subjectFrom: issue.title
```

## `mohist/github-pr-status`

Verifies that the specified GitHub Pull Request is in the expected state.

### Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `repositoryUrl` | Yes | - | Git repository URL that identifies the GitHub repository. The value is text. |
| `prNumber` | Yes | - | Pull Request number. The value is numeric. |
| `expect` | No | `open,ready` | Comma-separated expected states: `open`, `ready`, or `merged`. The value is text. |

### Outputs

| Field | Meaning |
|---|---|
| `kind` | Output type identifier. |
| `status` | Status identifier. |
| `prNumber` | Pull Request number. |
| `prUrl` | Pull Request URL. |
| `prState` | Pull Request state. |
| `isDraft` | Whether the Pull Request is a draft. |
| `expectations` | Expected-state markers. |
| `missing` | Expected-state markers that were not satisfied. |
| `output` | Aggregated `gh` output. |
| `steps` | `gh` command results for each step. |

### Business Error Codes

| Error code | Meaning |
|---|---|
| `pr-status-failed` | Pull Request state validation failed. |

### Example

```yaml
- id: verify-pr-status
  uses: mohist/github-pr-status
  with:
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
    expect: open,ready
```

## `mohist/github-pr-checks`

Waits for every check on the specified GitHub Pull Request to pass.

### Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `repositoryUrl` | Yes | - | Git repository URL that identifies the GitHub repository. The value is text. |
| `prNumber` | Yes | - | Pull Request number. The value is numeric. |

### Outputs

| Field | Meaning |
|---|---|
| `kind` | Output type identifier. |
| `status` | Check status identifier. |
| `prNumber` | Pull Request number. |
| `pollIntervalMs` | Polling interval in milliseconds. |
| `message` | User-facing check result. |
| `output` | Aggregated `gh` output. |
| `steps` | `gh` command results for each step. |

### Business Error Codes

| Error code | Meaning |
|---|---|
| `config-error` | GitHub configuration is missing or invalid. |
| `pr-checks-unavailable` | Pull Request check status is unavailable. |
| `pr-checks-failed` | Required Pull Request checks did not pass. |
| `aborted` | Polling was cancelled. |

### Example

```yaml
- id: wait-for-pr-checks
  uses: mohist/github-pr-checks
  with:
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
```
