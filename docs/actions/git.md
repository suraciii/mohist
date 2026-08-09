# Git Actions

Git Action repositories, branches, and remotes are determined by explicit
`with` inputs. An Action does not read implicit fallback values from Variables,
and Git commands always use the workspace supplied by the host.

In these examples, `${{ repository.baseBranch }}` and
`${{ workspace.branch }}` come from the repository and workspace for the
current run. `origin` is an explicitly selected remote name. See
[Workflow Definition Reference](../workflow-definition.md#template-expressions)
for the complete expression rules.

## `mohist/workspace-prepare`

Resets the workspace to the expected branch and removes residual local Git
state.

### Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `expectedBranch` | Yes | - | Expected workspace branch. The value is text. |

### Outputs

| Field | Meaning |
|---|---|
| `kind` | Output type identifier. |
| `status` | Status identifier. |
| `expectedBranch` | Expected branch name. |
| `head` | HEAD snapshot after preparation. |
| `residual` | Residual-state snapshot after preparation. |
| `porcelain` | Porcelain status after preparation. |
| `step` | Step that produced the snapshot on failure. |
| `workDir` | Workspace directory. |

### Business Error Codes

| Error code | Meaning |
|---|---|
| `workspace-setup` | Workspace preparation failed. |

### Example

```yaml
- id: prepare-workspace
  uses: mohist/workspace-prepare
  with:
    expectedBranch: ${{ workspace.branch }}
```

## `mohist/rebase`

Rebases the current branch onto the base branch and can optionally squash the
rebased commits into one commit.

### Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `baseBranch` | Yes | - | Base branch name. The value is text. |
| `remote` | No | - | Git remote name. The value is text. |
| `squash` | No | `false` | Whether to squash the rebased commits into one commit. The value is Boolean. |
| `message` | No | - | Explicit squash commit message. The value is text. |
| `messageFrom` | No | - | Issue field used as the squash commit message. The value is text. |

### Outputs

| Field | Meaning |
|---|---|
| `kind` | Output type identifier. |
| `status` | Rebase status identifier. |
| `baseBranch` | Base branch name. |
| `remote` | Git remote name. |
| `baseRef` | Resolved base reference. |
| `rebasedOntoSha` | Tip commit SHA of the base reference when rebase started. |
| `beforeHeadSha` | HEAD SHA before rebase. |
| `afterHeadSha` | HEAD SHA after rebase. |
| `squashed` | Whether the squash step ran. |
| `squashedHeadSha` | HEAD SHA after squash. |
| `rebased` | Whether rebase succeeded. |
| `conflicts` | Files with unresolved conflicts. |
| `rebaseLeftInProgress` | Whether a rebase was left in progress. |
| `output` | Aggregated Git output. |
| `steps` | Git command results for each step. |

### Business Error Codes

| Error code | Meaning |
|---|---|
| `abort-failed` | Aborting an existing rebase failed. |
| `fetch-failed` | Fetching the base branch failed. |
| `base-resolve-failed` | Resolving the base reference failed. |
| `prepare-failed` | Preparing the workspace before rebase failed. |
| `rebase-failed` | Rebase failed for an unspecified reason. |
| `conflict` | Rebase encountered conflicts. |
| `squash-failed` | The squash step failed. |

### Example

```yaml
- id: rebase-onto-base
  uses: mohist/rebase
  with:
    baseBranch: ${{ repository.baseBranch }}
    remote: origin
    squash: false
```

## `mohist/rebase-status`

Reports the current rebase state of the workspace.

### Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `baseBranch` | Yes | - | Base branch name. The value is text. |
| `remote` | No | - | Git remote name. The value is text. |

### Outputs

| Field | Meaning |
|---|---|
| `kind` | Output type identifier. |
| `status` | Status identifier: verified or failed. |
| `baseBranch` | Base branch name. |
| `remote` | Git remote name. |
| `baseRef` | Resolved base reference. |
| `rebaseInProgress` | Whether a rebase is in progress. |
| `conflicts` | Files with unresolved conflicts. |
| `baseSha` | Tip commit SHA of the base reference. |
| `headSha` | Current HEAD SHA. |
| `mergeBaseSha` | Merge-base SHA of HEAD and the base reference. |
| `output` | Aggregated Git output. |

### Business Error Codes

| Error code | Meaning |
|---|---|
| `rebase-incomplete` | Rebase is incomplete or the workspace is not clean. |

### Example

```yaml
- id: check-rebase
  uses: mohist/rebase-status
  with:
    baseBranch: ${{ repository.baseBranch }}
    remote: origin
```

## `mohist/merge-ready`

Reports whether the current workspace can merge into the base branch.

### Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `baseBranch` | Yes | - | Base branch name. The value is text. |
| `remote` | Yes | - | Git remote name. The value is text. |
| `source` | Yes | - | Source branch name. The value is text. |

### Outputs

| Field | Meaning |
|---|---|
| `kind` | Output type identifier. |
| `targetBranch` | Base branch name. |
| `strategy` | Merge strategy identifier. |
| `baseSha` | Tip commit SHA of the base reference. |
| `candidateHeadSha` | Tip commit SHA of the source reference. |
| `mergeBaseSha` | Merge-base SHA of the source and base branches. |
| `canMerge` | Whether the branches can merge. |
| `conflictFiles` | Files with unresolved conflicts. |
| `checkedAt` | ISO timestamp of the check. |

### Business Error Codes

| Error code | Meaning |
|---|---|
| `merge-not-ready` | The current state does not meet merge conditions. |

### Example

```yaml
- id: verify-merge-ready
  uses: mohist/merge-ready
  with:
    baseBranch: ${{ repository.baseBranch }}
    source: ${{ workspace.branch }}
    remote: origin
```

## `mohist/push`

Pushes the workspace source branch to a target branch.

### Inputs

| Field | Required | Default | Meaning |
|---|---:|---|---|
| `source` | Yes | - | Source branch. The value is text. |
| `target` | Yes | - | Target branch. The value is text. |
| `remote` | Yes | - | Git remote name. The value is text. |
| `force` | No | `false` | Whether to push with `--force`. The value is Boolean. |
| `forceWithLease` | No | `false` | Whether to push with `--force-with-lease`. The value is Boolean. |

### Outputs

| Field | Meaning |
|---|---|
| `kind` | Output type identifier. |
| `status` | Push status identifier. |
| `source` | Source branch. |
| `target` | Target branch. |
| `remote` | Git remote name. |
| `refspec` | Resolved refspec. |
| `workDir` | Workspace directory. |
| `landedCommit` | Tip commit that was pushed. |
| `pushed` | Whether the push succeeded. |
| `force` | Whether force mode was used. |
| `forceWithLease` | Whether force-with-lease mode was used. |
| `output` | Aggregated Git push output. |
| `steps` | Git command results for each step. |

### Business Error Codes

| Error code | Meaning |
|---|---|
| `base-moved` | The target branch moved, so the push is not a fast-forward update. |
| `push-failed` | Push failed for an unspecified reason. |

### Example

```yaml
- id: push-branch
  uses: mohist/push
  with:
    source: ${{ workspace.branch }}
    target: ${{ repository.baseBranch }}
    remote: origin
    force: false
    forceWithLease: false
```
