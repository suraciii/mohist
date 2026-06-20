## Why

Integrate's landing currently depends on a separate, disposable "landing clone" workspace. When issue #166's `mohist/prepare` rebase crashed mid-flight (runner-lost), the shared workflow workspace was left stuck in an unfinished rebase — `you need to resolve your current index first` — and every subsequent `git checkout runBranch` was refused, killing the issue. The landing clone is a redundant, fragile second workspace whose only consumers (publish and the merge-ready probe) can both be expressed on the single workflow workspace on the run branch, so removing it both simplifies delivery and gives the workflow workspace the crash-self-healing safety net it currently lacks.

## What Changes

- **BREAKING**: Delete `mohist/prepare` and `mohist/publish` actions and the entire landing-workspace mechanism (`createLandingWorkspace` / `disposeLandingWorkspace` / `pruneLandingWorkspaces`, landing paths in `workspace.ts`, all landing calls in `registry.ts`). No remaining code depends on landing once publish and the merge-ready probe stop using it.
- **BREAKING**: Merge `prepare` and `rebase.ts` into a single unified `mohist/rebase` action (they were near-duplicate rebase loops). New options:
  - `remote`: set → `fetch <remote> <baseBranch>` then rebase onto `<remote>/<baseBranch>` (former prepare behavior); unset → rebase local base (former rebase behavior). Integrate sets `origin`; manual rebase may leave it unset.
  - `squash` + `message`: after a successful rebase, `git reset --soft <base>` + `git commit` folds N commits into 1. `reset --soft` keeps the already-final work tree/index, so the squash phase cannot produce conflicts or new failure modes.
  - The manual `POST /{number}/rebase` endpoint and Integrate share this one action with different parameters.
- Add `mohist/push`: a pure fast-forward ref push, `git push origin <source>:<target>`. No target checkout, no landing clone, no working-tree mutation. Integrate sets source = run branch, target = base branch.
- Add a workspace health gate at the `verify`/`materialize` entry: when a residual rebase/merge state is detected (`rebase-merge` / `rebase-apply` / `MERGE_HEAD` / `CHERRY_PICK_HEAD`), abort it and recover to "on run branch, clean, aligned to the run branch ref". This is the only safety net once landing is gone and the crash self-healing mechanism. During rebase the run branch ref is not moved (advanced only on success), so work already committed by an agent is provably safe after a crash.
- **BREAKING**: Change the check-stage merge-ready preflight from a "landing clone `merge --squash --no-commit` probe" to a cheap `git merge-base --is-ancestor origin/<baseBranch> <runBranch>` check. No working tree, no clone.
- Rewrite the default workflow's Integrate stage to: `spec-sync → archive-change → rebase(remote=origin, squash=true) → push`. Final master holds a single squash commit and is a fast-forward.

## Capabilities

### New Capabilities
- `workspace-health-gate`: Entry-point self-healing for the workflow workspace — detecting residual rebase/merge/cherry-pick state at `verify`/`materialize` and aborting + recovering to the run branch, so a mid-flight runner crash never leaves the single workspace permanently stuck.

### Modified Capabilities
- `merge-delivery`: Delivery model collapses from the two-task `prepare → publish` (landing clone) into a single on-workspace `rebase(remote, squash) → push` flow; the unified rebase action (remote + squash options) and the new fast-forward push action become the delivery mechanism, with corresponding failure-kind updates.
- `workflow-definition`: The Integrate stage task list changes from `spec-sync, archive-change, prepare, publish` to `spec-sync, archive-change, rebase(remote=origin, squash=true), push`; REQ-WD-001 and REQ-WD-002 ordering and single-push-owner scenarios update accordingly.
- `worktree-manager`: The "Isolated temporary landing workspaces" requirement is removed (landing mechanism deleted), and the "Read-only squash mergeability preflight" requirement changes from a squash-merge probe to an `is-ancestor` check.
- `workspace-materialization`: The `mohist/prepare` action requirement is removed; its remote-fetch rebase behavior is absorbed by the unified rebase action operating inside the bound workspace.

## Impact

- **Runner actions** (`packages/runner/src/actions/`): delete `prepare` and `publish`; extend `rebase.ts` with `remote` + `squash`/`message`; add `push.ts`; rewrite `rebaseStatusAction` merge-ready probe to `is-ancestor`.
- **Action registry** (`packages/runner/src/actions/registry.ts`): remove all landing-workspace calls; register `push`.
- **Workspace runtime** (`packages/runner/src/runtime/workspace.ts`): delete landing methods; add the health gate in `verify`/`materialize`.
- **Workflow definition** (`packages/server/.../mohist-default.workflow.yaml`): rewrite the Integrate stage.
- **Tests**: update `issue-112-regression.spec.ts` and `merge-ready.spec.ts` to the new model; add tests for `push`, rebase `squash`, and the health gate (including a simulated mid-rebase crash workspace).
- **Risk** (high): rewrites the merge-to-master path; blast radius = every subsequent issue's landing. Cross-cutting refactor across runner actions, workflow definition, and workspace management.
