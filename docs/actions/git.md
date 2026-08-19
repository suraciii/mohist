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

```yaml
- id: prepare-workspace
  uses: mohist/workspace-prepare
  with:
    expectedBranch: ${{ workspace.branch }}
```

Inputs:

- `expectedBranch` (required, text): expected workspace branch.

Outputs:

- `kind`: output type identifier.
- `status`: status identifier.
- `expectedBranch`: expected branch name.
- `head`: HEAD snapshot after preparation.
- `residual`: residual-state snapshot after preparation.
- `porcelain`: porcelain status after preparation.
- `step`: step that produced the snapshot on failure.
- `workDir`: workspace directory.

Business error codes:

- `workspace-setup`: workspace preparation failed.

## `mohist/rebase`

Rebases the current branch onto the base branch and can optionally squash the
rebased commits into one commit.

```yaml
- id: rebase-onto-base
  uses: mohist/rebase
  with:
    baseBranch: ${{ repository.baseBranch }}
    remote: origin
    squash: false
```

Inputs:

- `baseBranch` (required, text): base branch name.
- `remote` (optional, text): Git remote name.
- `squash` (optional, Boolean, default `false`): whether to squash the rebased
  commits into one commit.
- `message` (optional, text): explicit squash commit message.
- `messageFrom` (optional, text): Issue field used as the squash commit
  message.

Outputs:

- `kind`: output type identifier.
- `status`: rebase status identifier.
- `baseBranch`: base branch name.
- `remote`: Git remote name.
- `baseRef`: resolved base reference.
- `rebasedOntoSha`: tip commit SHA of the base reference when rebase started.
- `beforeHeadSha`: HEAD SHA before rebase.
- `afterHeadSha`: HEAD SHA after rebase.
- `squashed`: whether the squash step ran.
- `squashedHeadSha`: HEAD SHA after squash.
- `rebased`: whether rebase succeeded.
- `conflicts`: files with unresolved conflicts.
- `rebaseLeftInProgress`: whether a rebase was left in progress.
- `output`: aggregated Git output.
- `steps`: Git command results for each step.

Business error codes:

- `abort-failed`: aborting an existing rebase failed.
- `fetch-failed`: fetching the base branch failed.
- `base-resolve-failed`: resolving the base reference failed.
- `prepare-failed`: preparing the workspace before rebase failed.
- `rebase-failed`: rebase failed for an unspecified reason.
- `conflict`: rebase encountered conflicts.
- `squash-failed`: the squash step failed.

## `mohist/rebase-status`

Reports the current rebase state of the workspace.

```yaml
- id: check-rebase
  uses: mohist/rebase-status
  with:
    baseBranch: ${{ repository.baseBranch }}
    remote: origin
```

Inputs:

- `baseBranch` (required, text): base branch name.
- `remote` (optional, text): Git remote name.

Outputs:

- `kind`: output type identifier.
- `status`: status identifier: verified or failed.
- `baseBranch`: base branch name.
- `remote`: Git remote name.
- `baseRef`: resolved base reference.
- `rebaseInProgress`: whether a rebase is in progress.
- `conflicts`: files with unresolved conflicts.
- `baseSha`: tip commit SHA of the base reference.
- `headSha`: current HEAD SHA.
- `mergeBaseSha`: merge-base SHA of HEAD and the base reference.
- `output`: aggregated Git output.

Business error codes:

- `rebase-incomplete`: rebase is incomplete or the workspace is not clean.

## `mohist/merge-ready`

Reports whether the current workspace can merge into the base branch.

```yaml
- id: verify-merge-ready
  uses: mohist/merge-ready
  with:
    baseBranch: ${{ repository.baseBranch }}
    source: ${{ workspace.branch }}
    remote: origin
```

Inputs:

- `baseBranch` (required, text): base branch name.
- `remote` (required, text): Git remote name.
- `source` (required, text): source branch name.

Outputs:

- `kind`: output type identifier.
- `targetBranch`: base branch name.
- `strategy`: merge strategy identifier.
- `baseSha`: tip commit SHA of the base reference.
- `candidateHeadSha`: tip commit SHA of the source reference.
- `mergeBaseSha`: merge-base SHA of the source and base branches.
- `canMerge`: whether the branches can merge.
- `conflictFiles`: files with unresolved conflicts.
- `checkedAt`: ISO timestamp of the check.

Business error codes:

- `merge-not-ready`: the current state does not meet merge conditions.

## `mohist/push`

Pushes the workspace source branch to a target branch.

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

Inputs:

- `source` (required, text): source branch.
- `target` (required, text): target branch.
- `remote` (required, text): Git remote name.
- `force` (optional, Boolean, default `false`): whether to push with `--force`.
- `forceWithLease` (optional, Boolean, default `false`): whether to push with
  `--force-with-lease`.

Outputs:

- `kind`: output type identifier.
- `status`: push status identifier.
- `source`: source branch.
- `target`: target branch.
- `remote`: Git remote name.
- `refspec`: resolved refspec.
- `workDir`: workspace directory.
- `landedCommit`: tip commit that was pushed.
- `pushed`: whether the push succeeded.
- `force`: whether force mode was used.
- `forceWithLease`: whether force-with-lease mode was used.
- `output`: aggregated Git push output.
- `steps`: Git command results for each step.

Business error codes:

- `base-moved`: the target branch moved, so the push is not a fast-forward
  update.
- `push-failed`: push failed for an unspecified reason.
