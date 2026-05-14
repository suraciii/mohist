## Findings

1. Error: Integrate accepts refreshed stale approval evidence and can continue side effects for an unapproved candidate.

File: `packages/cli/src/workflow/integrate-stage-runner.ts:235-257,279-348`

The stale-snapshot path logs `Approved merge-ready snapshot stale, refreshing`, reruns `checkSquashMergeability()`, and returns `{ valid: true }` when the refreshed preflight passes. That allows `executeTasks()` to continue into `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge` for a candidate whose approved `mergeReadySnapshot` no longer matches current base/head state. This conflicts with the proposal acceptance criterion `If the base branch changes after merge-ready passes, approval or Integrate preflight invalidates the old merge-ready result and asks for a re-check instead of trusting stale state`, and with design D5 (`design.md:81-83`) which says Integrate should fail locally and instruct a Check rerun when base/head changed after approval.

Suggested fix:

File: `packages/cli/src/workflow/integrate-stage-runner.ts`

- Split `approvedSnapshot` handling into three cases: missing, fresh, and stale.
- Keep the current refresh behavior only for missing evidence.
- When evidence is stale, either fail immediately with a `409`-style local workflow error, or run one diagnostic preflight and always return `{ valid: false }` with `refreshed: true` plus an explicit `Re-run Check` error message, even if `snapshot.canMerge === true`.
- Add a regression test proving stale-but-currently-mergeable evidence still stops before `integrate:spec-sync`/`integrate:archive-change` and instructs a Check rerun.

## Spec Compliance

1. Acceptance criterion: `merge-ready` runs a real merge preflight equivalent to Mohist's final squash merge semantics, without mutating `master` or the issue branch.
PASS
Evidence: `packages/cli/src/git/worktree-manager.ts:990-1192` creates a detached temporary worktree at `baseSha`, runs `git merge --squash <candidateHeadSha>`, and removes the temporary worktree. Regression coverage exists in `packages/cli/tests/workflow/merge-ready.test.ts:232-253,321-343`.

2. Acceptance criterion: A branch that would fail `git merge --squash <issue-branch>` against the current base causes `merge-ready` to fail.
PASS
Evidence: `packages/cli/src/workflow/checks/merge-ready-check.ts:32-58` maps `snapshot.canMerge` directly to pass/fail. Real Git regression coverage exists in `packages/cli/tests/workflow/merge-ready.test.ts:232-312`.

3. Acceptance criterion: `merge-ready` output includes structured mergeability facts such as `baseSha`, `candidateHeadSha`, `mergeBaseSha`, `targetBranch`, `canMerge`, `conflictFiles`, and the checked merge strategy.
PASS
Evidence: `packages/cli/src/workflow/checks/merge-ready-check.ts:47-58` returns all required fields. `packages/cli/src/workflow/base-stage-runner.ts:506-525` persists that output into approval state as `mergeReadySnapshot`.

4. Acceptance criterion: Approval for Check stage validates that the approved `merge-ready` snapshot still matches the current base/head state.
PASS
Evidence: `packages/cli/src/api/issues.ts:1584-1689` validates presence, shape, `canMerge`, current base SHA, candidate head SHA, merge-base SHA, and target branch before approval. Regression coverage exists in `packages/cli/tests/api-routes.test.ts:769-907`.

5. Acceptance criterion: If the base branch changes after `merge-ready` passes, approval or Integrate preflight invalidates the old merge-ready result and asks for a re-check instead of trusting stale state.
FAIL
Evidence: approval correctly rejects stale evidence in `packages/cli/src/api/issues.ts:1658-1689`, but Integrate does not. `packages/cli/src/workflow/integrate-stage-runner.ts:235-257,279-348` refreshes stale evidence and proceeds when the refreshed preflight passes. That trusts unapproved refreshed state instead of forcing a Check rerun.

6. Acceptance criterion: Integrate performs a final preflight before side-effectful delivery steps such as spec sync/archive when the mergeability snapshot is missing or stale.
PASS with deviation
Evidence: `packages/cli/src/workflow/integrate-stage-runner.ts:122-154,204-348` runs `validateMergeability()` before `runSpecSyncStep()` and `runArchiveStep()`. Missing evidence is handled correctly. Stale evidence is preflighted, but the stale-success behavior above violates the required stop-and-rerun-Check semantics.

7. Acceptance criterion: Integrate still treats the actual merge as the final authority and reports structured conflict files if a race occurs after preflight.
PASS
Evidence: `packages/cli/src/git/worktree-manager.ts:932-946` captures conflict files before cleanup on final squash-merge failure, and `packages/cli/src/workflow/integrate-stage-runner.ts:700-745` surfaces `targetBranch`, `strategy`, `baseSha`, `candidateHeadSha`, `mergeBaseSha`, `conflictFiles`, and `error`. Regression coverage exists in `packages/cli/tests/workflow/merge-ready.test.ts:588-740`.

8. Acceptance criterion: Regression coverage reproduces the #207 class of failure: `merge-ready` must fail for a branch whose final squash merge conflicts even when `conflictingFiles` is empty.
PASS
Evidence: `packages/cli/tests/workflow/merge-ready.test.ts:231-312` creates a real Git fixture where the issue worktree has no active rebase conflict state and `checkSquashMergeability()` still reports `canMerge: false` with conflict files.

## Review Dimensions

1. Correctness
FAIL due to the stale-approval Integrate behavior above.

2. Complexity
PASS
Evidence: the touched functions are moderate in size; `isApprovedSnapshotFresh()` is small and the new API-route validation is straightforward. No obvious cyclomatic-complexity spike beyond the existing route handler structure.

3. Test Coverage
PASS with warning
Evidence: targeted tests passed via `npm test -- --run tests/workflow/merge-ready.test.ts tests/api-routes.test.ts`. Warning: there is no regression test for the stale-but-still-mergeable Integrate case, which is the missing spec guard behind the correctness failure.

4. Security
PASS
Evidence: no new secret exposure paths found; Git commands use fixed argument arrays, not shell interpolation.

<promise>FAIL</promise>
