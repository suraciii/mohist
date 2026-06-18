## Why

A workflow-owned workspace is created for one run and checked out to that run's branch, but later integration actions violate that boundary: `integrate:publish` and the `merge-ready` preflight check out the repository base branch (e.g. `master`) *inside* the workflow workspace to land or test the squash commit. Once an action switches the workspace to the base branch, a failed or partially recovered run leaves the workspace in a surprising state, and the next retry has to distinguish actual issue work from branch-switch side effects. Recovery and future integration-drift repair cannot be safe and mechanical unless the workspace is branch-stable for the whole run — the run branch must be a runtime invariant, not just the initial checkout.

## What Changes

- **Establish a branch-stability runtime invariant.** A workflow-owned workspace SHALL remain on its `workspace.branch` for the entire workflow run. Actions MAY fetch, rebase the run branch onto the remote base, inspect refs, create isolated temporary workspaces, and push refs, but they SHALL NOT check out the repository base branch inside the workflow workspace.
- **Add branch-stability verification at task boundaries.** Workflow task execution SHALL verify the workspace starts *and* ends on the expected `workspace.branch`, mirroring the existing clean-worktree evidence. A task that starts or ends on the wrong branch is a runner/action bug, not ordinary task failure.
- **Make publish branch-stable.** **BREAKING (runner-internal):** `integrate:publish` SHALL NOT check out the base branch in the workflow workspace. Landing the single squash commit SHALL happen through a branch-stable mechanism — an isolated temporary landing workspace or equivalent ref-safe operation — so the issue workspace never leaves its run branch.
- **Make merge-ready preflight branch-stable.** The `merge-ready` squash-mergeability preflight SHALL be read-only/ref-safe and SHALL NOT check out the base branch inside the workflow workspace; it SHALL leave the run branch ref unchanged.
- **Keep prepare branch-stable (codify current behavior).** `integrate:prepare` already rebases the run branch onto the remote base without checking out the base branch; this SHALL be preserved as a requirement so it does not regress.
- **Distinguish branch-invariant violations in diagnostics.** Failure messages SHALL separate branch-invariant violations from ordinary dirty-worktree, conflict, base-moved, or provider failures, surfacing them as runner/action bugs with clear evidence.
- **Make retry recovery mechanical.** Retrying a failed workflow task SHALL NOT require manually restoring the workspace from `master` back to the run branch.
- **No change to the user-visible delivery outcome.** An issue's changes still land on the base branch as exactly one clean commit and are pushed to the remote.

## Capabilities

### New Capabilities
- `workspace-branch-stability`: The runtime invariant that a workflow-owned workspace stays on its run branch for the entire workflow run. Defines the domain boundary (workflow workspace, run branch, base branch as a ref-only target, landing commit constructed outside the workspace), the start/end branch-stability checks at task boundaries, and the branch-invariant-violation diagnostics classification. This is the foundation that makes recovery and future integration-drift repair safe. Becomes `specs/workspace-branch-stability/spec.md`.

### Modified Capabilities
- `merge-delivery`: Publish SHALL land via a branch-stable mechanism (isolated temporary landing workspace or ref-safe operation) instead of checking out the base branch in the workflow workspace; prepare is codified as branch-stable (rebase the run branch onto the remote base without checking out the base branch). The delivery failure classification gains a branch-invariant-violation kind distinct from dirty-worktree, conflict, base-moved, and retry-safe failures, and the "failed delivery leaves a clean workspace" requirement is strengthened to also leave the workspace on the run branch.
- `worktree-manager`: The squash-mergeability preflight SHALL become read-only/ref-safe and SHALL NOT check out the base branch inside the workflow workspace. WorktreeManager SHALL support creating isolated temporary landing workspaces (or an equivalent ref-safe mechanism) so publish can land without switching the workflow workspace.
- `workflow-run`: Task completion SHALL require branch-stability verification evidence (task starts and ends on `workspace.branch`) alongside the existing clean-worktree evidence; a task missing that evidence, or starting/ending on the wrong branch, SHALL be treated as incomplete / a runner-action bug rather than a generic failure, so retry and recovery can assume the checkout is the run branch.

## Impact

- **Runner actions** (`packages/runner/src/actions/registry.ts`): `publishAction` (currently `git checkout <target>` at the publish boundary, followed by in-workspace `merge --ff-only`, `merge --squash`, `commit`, `push`) is reworked to land through an isolated temporary landing workspace or ref-safe path without leaving the run branch. `runSquashMergePreflight` (used by `mergeReadyAction`, currently `git checkout <target>` then restore) is made read-only/ref-safe. `prepareAction` is confirmed branch-stable and protected from regression.
- **Runner workspace runtime** (`packages/runner/src/runtime/workspace.ts`): gains the ability to materialize isolated temporary landing workspaces for branch-stable publish, in addition to the existing per-run workspace checkout (which already correctly stays on the run branch).
- **Task boundary verification** (`packages/runner/src/runtime/executor.ts` and task handlers): add start/end branch checks against `workspace.branch`, reported as branch-stability evidence alongside clean-worktree evidence.
- **Failure reporting (CLI and Web UI):** branch-invariant violations are surfaced as a distinct, evidence-backed runner/action-bug failure kind across task/log/evidence surfaces, separate from dirty-worktree, conflict, base-moved, and provider failures.
- **High-risk validation paths:** prepare, merge-ready, publish, retry, and live workflow recovery must all be validated against the new invariant; existing integration behavior must still produce a single clean landing commit on the remote base branch.
- **No user-facing configuration change:** issues still land on the base branch as one commit and are pushed to the remote; only the internal branch context in which the landing is constructed changes.
