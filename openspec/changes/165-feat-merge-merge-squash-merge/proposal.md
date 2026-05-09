## Why

The current merge flow preserves every issue-branch commit on the base branch, so implementation details like planning commits and task status updates dominate `master` history. For a workflow where one issue is the user-visible unit of change, forcing squash merge now makes the mainline history readable, easier to revert, and better aligned with how users reason about completed issues while keeping detailed task-level history on the feature branch.

## What Changes

- **BREAKING**: Issue completion no longer fast-forwards issue branches into the base branch; every successful issue merge lands as exactly one squash commit.
- Remove the fast-forward merge path from both legacy `mergeBack()` and integrate-stage `mergeApprovedCandidate()` flows.
- Generate the squash commit message from the issue title and the change directory `tasks.json`, so the single mainline commit summarizes the issue and completed work instead of exposing internal task-update commits.
- Remove `fastForward` from merge result contracts and stage-context propagation because it is no longer a possible merge outcome.
- Update tests to assert squash-only behavior and remove fast-forward-only expectations.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `worktree-manager`: Issue merge-back behavior changes from fast-forward/rebase-plus-fast-forward to mandatory squash merge with a generated issue-level commit message.

## Impact

- `packages/cli/src/git/worktree-manager.ts`: `mergeBack()`, `mergeApprovedCandidate()`, merge result types, and commit-message generation behavior.
- `packages/cli/src/workflow/stage-context.ts`: WorktreeManager merge contract removes `fastForward` from successful merge results.
- `packages/cli/src/workflow/integrate-stage-runner.ts`: Integrate-stage merge summaries, checkpoints, and emitted output stop reporting fast-forward status.
- `packages/cli/src/git/merge-queue.ts`: Merge logging and assumptions change from fast-forward merge completion to squash merge completion.
- Change artifact reads: squash commit message generation depends on the issue title and `tasks.json` in the issue change directory.
- Tests covering merge-back, integrate-stage merge, stage-context typings, and fast-forward-only paths must be updated to reflect the forced squash strategy.
- No new runtime dependencies or user-facing configuration options are introduced.
