# Git Actions

Git delivery inputs are explicit `with` fields. Actions do not fall back to
repository or workspace Variables, and the host-provided `workDir` is always
used for Git commands.

## `mohist/workspace-prepare`

Required inputs:

- `expectedBranch` (`string`)

The action cleans residual local Git state and aligns the host workspace with
the declared branch. It does not select a branch or path from Variables.

## `mohist/rebase`

Required inputs:

- `baseBranch` (`string`)

Optional inputs:

- `remote` (`string`)
- `squash` (`boolean`, default `false`)
- `message` (`string`)
- `messageFrom` (`string`)

## `mohist/rebase-status`

Required inputs:

- `baseBranch` (`string`)

Optional inputs:

- `remote` (`string`)

## `mohist/merge-ready`

Required inputs:

- `baseBranch` (`string`)
- `source` (`string`)
- `remote` (`string`)

## `mohist/push`

Required inputs:

- `source` (`string`)
- `target` (`string`)
- `remote` (`string`)

Optional inputs:

- `force` (`boolean`, default `false`)
- `forceWithLease` (`boolean`, default `false`)
