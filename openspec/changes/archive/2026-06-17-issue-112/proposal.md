## Why

Agent-backed workflow tasks can report success while the git worktree still contains uncommitted changes. Later Integrate steps inherit that dirty workspace and fail unpredictably — typically at merge or push — even though the real failure happened earlier when a previous task left its work uncommitted. The `mohist/merge` action today creates a local merge commit without verifying clean worktree or pushing to the remote, so users cannot trust that merge means "the issue was merged into the latest remote base branch as one clean squash commit."

## What Changes

- **Task completion invariant**: no task is marked completed until `git status --porcelain` is clean in the task workspace. Applies to agent-backed and deterministic actions alike.
- **Agent cleanup loop**: when an agent-backed task returns with uncommitted changes, Mohist sends a bounded follow-up prompt to the same agent session asking it to commit task-related changes or revert unrelated changes, then re-verifies cleanliness.
- **Structured dirty-worktree evidence**: when a task fails after exhausting cleanup attempts, the failure output includes staged, unstaged, and untracked file lists.
- **Merge action rewrite**: `mohist/merge` now owns the full delivery flow — rebase source onto latest fetched remote target, create one squash landing commit from the remote target HEAD, and fast-forward push that landing commit when `push: true`.
- **Remove push as separate concern**: `mohist/merge` with `push: true` makes push part of the merge completion contract. The default workflow does not expose `integrate:push` as a standalone user-facing task.
- **Merge boundary guard**: `mohist/merge` checks the source worktree is clean before starting and refuses to merge dirty leftovers from earlier tasks.
- **Remote-advanced retry**: if the remote target advances between fetch/rebase and push, the merge action fetches the new remote target, rebases again, regenerates the landing commit, and retries within a bounded limit.
- **spec-sync commitment**: `integrate:spec-sync` prompt tells the agent to commit generated spec changes or report a no-change result; the runner verifies clean worktree after the agent returns.
- **Structured failure classification**: issue detail and CLI failure messages identify whether the failure came from task cleanup, source rebase conflict, landing commit validation, or fast-forward push.

## Capabilities

### New Capabilities

- `task-cleanup`: clean git worktree invariant for task completion, bounded agent cleanup loop that uses the same agent session to commit or revert leftover changes, and structured dirty-worktree evidence output for exhausted-cleanup failures.
- `merge-delivery`: rewritten `mohist/merge` action that fetches the latest remote target, rebases the source branch onto it, creates one squash landing commit from the fetched remote target HEAD, fast-forward pushes the landing commit when `push: true`, retries on remote-advanced races, and reports structured failure evidence for each phase (cleanup block, rebase conflict, landing validation, fast-forward push).

### Modified Capabilities

- `workflow-run`: REQ-WR-005 Integrate task list and delivery facts — merge now produces push evidence (remote ref verification, remote-advanced retry facts); task completion evidence must include clean-worktree verification.
- `workflow-definition`: REQ-WD-001/002 default Integrate task ordering — `integrate:merge` now includes `push: true` in its `with` block; spec-sync task must commit or report no-change before completing; the default workflow has no standalone `integrate:push` task.
- `workflow-engine`: REQ-WFE-005 spec-sync commits spec changes and verifies clean worktree; merge preflight validates clean source worktree before starting delivery.
- `workflow-agent`: agent-backed task completion requires clean worktree verification after the agent returns; the cleanup loop interacts through the same agent session reference without starting new work.
- `pipeline-model`: REQ-PM-007 Integrate failure locality — merge task failure covers push and remote-advanced failures as part of the same task; post-merge push failure is a delivery failure within `integrate:merge`, not a separate push task failure.
- `worktree-manager`: merge boundary adds a source-worktree cleanliness check before the merge action mutates the target branch.

## Impact

- `packages/runner/src/actions/registry.ts` — rewrite `mergeAction` to implement the full fetch→rebase→squash-land→push flow, add `push: true`, `pushRemote`, and `maxPushRetry` inputs, insert source-worktree clean guard, add remote-advanced retry loop, and produce structured phase failure evidence.
- `packages/runner/src/actions/openspec.ts` — update `openspecSyncAction` prompt to instruct the agent to commit spec changes or report no-change.
- `packages/runner/src/actions/acp-agent.ts` — add agent cleanup loop support: after agent returns, check worktree cleanliness, send follow-up prompt, retry within bound.
- `packages/runner/src/runtime/executor.ts` — add post-action `git status --porcelain` check in `executeOne()` and `executeChecks()`; for agent-backed tasks, enter cleanup loop on dirty worktree.
- `packages/runner/src/runtime/host.ts` — structured dirty-worktree failure reporting.
- `packages/runner/src/core/types.ts` — add dirty-worktree evidence shape to `WorkItemResult`.
- `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-default.workflow.yaml` — add `push: true` and `remote: origin` to `integrate:merge` `with` block; update spec-sync prompt to instruct committing changes.
- `packages/server/.../Workflow/Grains/WorkflowGrain.cs` — handle new structured failure evidence from dirty worktree and merge phases.
- Tests: add regression coverage reproducing the #102 shape (agent task leaves changes → cleanup requested → merge refused until clean → successful merge+push in one action); update existing merge tests for the new flow.
