# GitHub PR Actions

GitHub PR Action repositories, branches, and Pull Request identities are
determined by explicit `with` inputs. An Action does not read implicit fallback
values from Variables and always uses the working directory supplied by the
host.

In these examples, `${{ repository.gitUrl }}`,
`${{ repository.baseBranch }}`, `${{ repository.path }}`, and
`${{ repository.branch }}` come from the target Repository for the current run.
`${{ vars.github.pr.number }}`
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
    working-directory: ${{ repository.path }}
    repositoryUrl: ${{ repository.gitUrl }}
    source: ${{ repository.branch }}
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
    working-directory: ${{ repository.path }}
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
```

## `mohist/merge-github-pr`

Enables GitHub Auto-merge with the squash method for the specified Pull Request
and waits until GitHub reports the Pull Request state as `MERGED`. It succeeds
immediately when that Pull Request is already merged at the expected reviewed
head. Enabling Auto-merge is not completion: the Action reports success only
after the merged state, reviewed head, and merge commit are confirmed from
GitHub.

### Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `repositoryUrl` | Yes | - | Git repository URL that identifies the GitHub repository. The value is text. |
| `method` | No | `squash` | Merge method. Only `squash` is supported. The value is text. |
| `prNumber` | Yes | - | Pull Request number. The value is numeric. |
| `expectedHeadSha` | Yes | - | Exact Pull Request head commit accepted by the reviewer. The value is text. |
| `subject` | No | - | Explicit squash-commit subject. The value is text. |
| `subjectFrom` | No | `issue.title` | Issue field used as the squash-commit subject. The value is text. |

### Outputs

| Field | Meaning |
|---|---|
| `kind` | Output type identifier. |
| `status` | Merge status identifier. |
| `prNumber` | Pull Request number. |
| `prUrl` | Pull Request URL. |
| `headSha` | Pull Request head commit that was merged. |
| `mergeCommitSha` | SHA of the squash-merge commit. |
| `method` | Merge method that was used. |
| `autoMergeEnabled` | Whether this invocation enabled Auto-merge; false when it was already enabled or merged. |
| `prState` | Confirmed terminal Pull Request state, always `MERGED` on success. |
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
| `pr-head-mismatch` | The live Pull Request head differs from `expectedHeadSha`. |
| `merge-failed` | Merging the Pull Request failed. |

The Action checks the live Pull Request head before enabling Auto-merge and on
every subsequent poll. A mismatch produces `pr-head-mismatch`. A closed,
unmerged Pull Request produces `pr-state-conflict`, and failed required checks
produce `pr-checks-failed` so Profile recovery can repair and publish another
commit.

Once this invocation enables Auto-merge, it owns that queued external operation.
Cancellation, deadline expiry, or any other non-success result is not terminal
until the Action confirms that Auto-merge is disabled or observes that the
expected head already merged. Runner loss supersedes the execution attempt but
keeps its Task nonterminal and schedules reconciliation work. Reconciliation
first inspects this Pull Request, reports success if the expected head merged,
or disables Auto-merge before the Task may fail or retry. Mohist therefore never
records a terminal non-success while GitHub can still merge the queued head.

### Example

```yaml
- id: merge-pr
  uses: mohist/merge-github-pr
  with:
    working-directory: ${{ repository.path }}
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
    expectedHeadSha: ${{ vars.github.reviewedHead }}
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
| `headSha` | Current Pull Request head commit. |
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
    working-directory: ${{ repository.path }}
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
| `expectedHeadSha` | Yes | - | Exact Pull Request head commit accepted by the reviewer. The value is text. |

### Outputs

| Field | Meaning |
|---|---|
| `kind` | Output type identifier. |
| `status` | Check status identifier. |
| `prNumber` | Pull Request number. |
| `headSha` | Pull Request head commit whose checks passed. |
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
| `pr-head-mismatch` | The live Pull Request head differs from `expectedHeadSha`. |
| `aborted` | Polling was cancelled. |

### Example

```yaml
- id: wait-for-pr-checks
  uses: mohist/github-pr-checks
  with:
    working-directory: ${{ repository.path }}
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
    expectedHeadSha: ${{ vars.github.reviewedHead }}
```

## Implementation Gap

The current `mohist/merge-github-pr` implementation attempts the merge directly.
It does not yet enable GitHub Auto-merge and wait for a confirmed `MERGED`
state, bind checks and merge to a reviewed head SHA, or reconcile queued
Auto-merge after interrupted execution. GitHub Actions also still receive the
checkout at the Workspace root rather than through
`working-directory: ${{ repository.path }}`.
