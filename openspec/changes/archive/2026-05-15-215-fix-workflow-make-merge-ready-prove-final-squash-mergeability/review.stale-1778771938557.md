## Findings

1. Error: Integrate accepts an approved snapshot without validating `mergeBaseSha`, so stale branch relationships can slip through.
File: `packages/cli/src/workflow/integrate-stage-runner.ts:205-243`
The spec requires Integrate freshness validation against base branch, candidate head, merge base, target branch, and `canMerge`. The current fast-path only checks `baseSha`, `candidateHeadSha`, `targetBranch`, and `canMerge`, then returns the approved snapshot as valid. A changed merge-base can therefore be treated as fresh and side effects can start before a refresh. Suggested fix: include `mergeBaseSha` in the fast-path comparison, and only reuse approved evidence when all required identity fields match current Git state.

2. Error: Check approval validation fails open when Git freshness checks cannot be completed.
File: `packages/cli/src/api/issues.ts:1642-1690`
The approval path is supposed to reject missing or stale mergeability evidence before enqueueing Integrate. Instead, any failure in `rev-parse` / `merge-base` validation is only logged and approval continues. This allows approval to succeed even when the snapshot cannot be proven current. Suggested fix: replace the warning-only catch with a `409` failure that instructs the user to rerun Check whenever mergeability freshness cannot be validated.

3. Error: Approval staleness tests do not exercise the real approval rejection path.
File: `packages/cli/tests/workflow/merge-ready.test.ts:415-485`
The acceptance criterion requires tests proving that base SHA, candidate head SHA, merge-base SHA, and target-branch changes reject old merge-ready evidence. These tests only assert that fake values differ from current values; they never call the approval API or any validation function, so they cannot catch regressions in approval behavior. Suggested fix: add API- or workflow-level tests that submit approval with stale `mergeReadySnapshot` values and assert `409` rejection for each field.

4. Error: The final-merge race regression test never reaches the authoritative merge step.
File: `packages/cli/tests/workflow/merge-ready.test.ts:660-798`
This test is labeled as coverage for post-preflight race conflicts, but it creates the conflict before Integrate starts and then asserts `integrate:preflight` fails and `integrate:merge` is absent. That only re-tests preflight failure, not the required behavior where preflight passes and the later authoritative squash merge reports structured conflict files. Suggested fix: add a test that forces a race after preflight success, then assert `mergeApprovedCandidate()` or `integrate:merge` fails with `targetBranch`, `strategy`, `conflictFiles`, and available SHA fields.

## Spec Compliance

1. `workflow-engine/spec.md` Requirement: Check merge-ready uses squash merge semantics
PASS
Evidence: `packages/cli/src/workflow/checks/merge-ready-check.ts:32-58`, `packages/cli/src/workflow/checks/merge-readiness-check.ts:32-58`, `packages/cli/src/git/worktree-manager.ts:990-1192`, regression coverage in `packages/cli/tests/workflow/merge-ready.test.ts:231-412`.

2. `workflow-engine/spec.md` Requirement: Check approval validates mergeability snapshot freshness
FAIL
Deviation: approval validation logs and continues on Git validation failure instead of failing closed (`packages/cli/src/api/issues.ts:1685-1690`), and the new tests do not prove actual rejection behavior (`packages/cli/tests/workflow/merge-ready.test.ts:415-485`).

3. `workflow-engine/spec.md` Requirement: Integrate preflights before side effects
FAIL
Deviation: Integrate reuses approved evidence without checking `mergeBaseSha` (`packages/cli/src/workflow/integrate-stage-runner.ts:205-243`), so stale mergeability snapshots are not fully validated before side effects.

4. `workflow-run/spec.md` Requirement: Persist merge-ready evidence for approval and diagnostics
PASS
Evidence: Check approval output includes `mergeReadySnapshot` in `packages/cli/src/workflow/base-stage-runner.ts:506-525`; Integrate records `integrate:preflight` diagnostics in `packages/cli/src/workflow/integrate-stage-runner.ts:222-242` and `:261-320`.

5. `worktree-manager/spec.md` Requirement: Read-only squash mergeability preflight
PASS
Evidence: `checkSquashMergeability()` creates a detached temp worktree, performs `git merge --squash`, captures conflicts before cleanup, and removes the temp worktree in `packages/cli/src/git/worktree-manager.ts:1080-1192`.

6. `worktree-manager/spec.md` Requirement: Authoritative final squash merge diagnostics
FAIL
Deviation: the implementation appears to return structured merge failure data in `packages/cli/src/git/worktree-manager.ts:932-946`, but the required post-preflight race coverage is missing because the only new AC-5 test fails in preflight and never exercises `integrate:merge` (`packages/cli/tests/workflow/merge-ready.test.ts:660-798`).

## Other Checks

1. Correctness: FAIL
2. Complexity: PASS
Evidence: touched functions stay within reasonable size except existing large methods; no new excessive branching found beyond the correctness issues above.
3. Test Coverage: FAIL
Evidence: `npm test -- --run tests/workflow/merge-ready.test.ts tests/workflow/check-integration-readiness.test.ts` passes, but two required regression areas are not meaningfully covered.
4. Security: PASS
Evidence: no obvious injection or secret-handling issues introduced in the reviewed changes.
5. Build: PASS
Evidence: `npm run build` succeeded in `packages/cli`.

<promise>FAIL</promise>
