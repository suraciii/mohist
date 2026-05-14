## Findings

1. Error: Check approval can silently skip merge-ready freshness validation when the local issue worktree is absent.

File: `packages/cli/src/api/issues.ts:1619-1691`

The stale-snapshot validation for `baseSha`, `candidateHeadSha`, `mergeBaseSha`, and `targetBranch` only runs inside `if (worktreePath) { ... }`. `worktreePath` comes from `worktreeManager.getPath(...)`, which returns `null` whenever the local worktree directory is missing (`packages/cli/src/git/worktree-manager.ts:777-780`). In that case the API still has enough information to validate freshness from the main repo refs (`git rev-parse <base>`, `git rev-parse mo/issue-N`, `git merge-base ...`), but it skips the merge-ready snapshot comparison entirely and can approve stale evidence.

This violates `workflow-engine/spec.md` Requirement `Check approval validates mergeability snapshot freshness`, because approval is required to reject stale evidence before enqueueing Integrate, not only when a local worktree path happens to exist.

Suggested fix:

File: `packages/cli/src/api/issues.ts:1619-1691`

Move the merge-ready snapshot freshness validation out of the `if (worktreePath)` block. Keep the worktree-only checks (`current HEAD` vs `approvalSnapshotSha`, `isWorktreeClean`) conditional on `worktreePath`, but always validate `baseSha`, `candidateHeadSha`, `mergeBaseSha`, `targetBranch`, and `canMerge` against Git refs in `project.path`. If resolving those refs fails, return `409` and fail closed.

## Spec Compliance

### workflow-engine/spec.md

- PASS: `Check merge-ready uses squash merge semantics`
  Evidence: `packages/cli/src/workflow/checks/merge-ready-check.ts:31-58` delegates pass/fail directly to `checkSquashMergeability()`. `packages/cli/src/workflow/checks/merge-readiness-check.ts:31-58` also delegates to the same API. Regression coverage exists in `packages/cli/tests/workflow/merge-ready.test.ts:231-317` and clean-pass coverage in `:320-412`.

- FAIL: `Check approval validates mergeability snapshot freshness`
  Deviation: `packages/cli/src/api/issues.ts:1619-1691` only compares `baseSha`, `candidateHeadSha`, `mergeBaseSha`, and `targetBranch` when `worktreeManager.getPath(...)` returns a local worktree path. If the worktree directory is missing, stale mergeability evidence is not rejected before approval.

- PASS: `Integrate preflights before side effects`
  Evidence: `packages/cli/src/workflow/integrate-stage-runner.ts:122-154` runs `validateMergeability()` before spec sync, archive, and merge. Fresh/stale handling is implemented in `:204-349`. Tests cover missing/stale preflight blocking side effects in `packages/cli/tests/workflow/merge-ready.test.ts:415-630`.

### workflow-run/spec.md

- PASS: `Persist merge-ready evidence for approval and diagnostics`
  Evidence: Check approval output includes `mergeReadySnapshot` in `packages/cli/src/workflow/base-stage-runner.ts:506-525`. Integrate records refreshed preflight diagnostics in task output at `packages/cli/src/workflow/integrate-stage-runner.ts:236-257` and `:287-346` without mutating approval output.

### worktree-manager/spec.md

- PASS: `Read-only squash mergeability preflight`
  Evidence: `packages/cli/src/git/worktree-manager.ts:990-1192` resolves `baseSha`, `candidateHeadSha`, `mergeBaseSha`, creates a detached temporary worktree, runs `git merge --squash`, captures conflict files before cleanup, and removes the temp worktree. Conflict-path regression test: `packages/cli/tests/workflow/merge-ready.test.ts:231-259`. Clean-path test: `:320-349`.

- PASS: `Authoritative final squash merge diagnostics`
  Evidence: `packages/cli/src/git/worktree-manager.ts:932-946` captures conflict files before cleanup on the real final squash merge failure and returns `targetBranch`, `strategy`, `baseSha`, `candidateHeadSha`, and `mergeBaseSha`. Race/final-merge coverage is exercised in `packages/cli/tests/workflow/merge-ready.test.ts` and confirmed by the focused test run.

## Test Coverage

- PASS: Focused tests passed with `npx vitest run tests/workflow/merge-ready.test.ts tests/api-routes.test.ts`.
- Warning: I did not find coverage for the failing case where Check approval must reject stale merge-ready evidence even when the local issue worktree path is unavailable.

## Overall

Overall result: FAIL due to the approval freshness gap above.

<promise>FAIL</promise>
