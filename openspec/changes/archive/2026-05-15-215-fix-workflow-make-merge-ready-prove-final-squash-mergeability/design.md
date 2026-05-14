## Context

`merge-ready` currently infers mergeability from `canFastForward`, `getWorktreeStatus()`, and the absence of `conflictingFiles`. That only describes the current issue worktree or an active rebase conflict, while Integrate's actual delivery merge checks out the base branch and runs `git merge --squash <issue-branch>`.

The design needs one shared mergeability truth that Check can run without mutating the base branch or issue branch, approval can validate for staleness, and Integrate can refresh before side-effectful delivery steps. Checks must remain read-only validators, and the real Integrate merge must remain the final authority.

## Goals / Non-Goals

**Goals:**

- Make `merge-ready` prove whether the current issue branch can be squash-merged into the current base branch using Mohist's final merge semantics.
- Return structured mergeability evidence that can be persisted, displayed, and compared for staleness.
- Reject Check approval when the approved mergeability snapshot no longer matches the current base/head state.
- Stop Integrate before spec sync/archive when mergeability evidence is absent, stale, or failing.
- Preserve structured conflict files on both preflight and final merge failures.

**Non-Goals:**

- Do not replace the final `mergeApprovedCandidate()` merge with preflight evidence.
- Do not add new user-facing workflow statuses or review UI.
- Do not make Integrate roll back already-applied side effects.
- Do not teach UI/API clients to recompute mergeability themselves.

## Decisions

### D1: Add a WorktreeManager mergeability preflight API

Add a single `checkSquashMergeability(projectPath, projectName, issueNumber, baseBranch)` method to `WorktreeManager` and `StageContext.WorktreeManager`. It returns a `MergeabilitySnapshot` shape:

```ts
interface MergeabilitySnapshot {
  kind: 'merge-ready';
  strategy: 'squash';
  targetBranch: string;
  baseSha: string;
  candidateHeadSha: string;
  mergeBaseSha: string;
  canMerge: boolean;
  conflictFiles: string[];
  checkedAt: string;
  error?: string;
}
```

The method owns all Git mechanics: resolving refs, running the temporary merge attempt, extracting conflict files, cleanup, and normalizing failures. `MergeReadyCheck`, approval validation, and Integrate only consume the snapshot and do not duplicate Git command knowledge.

**Alternatives considered:** Keep the logic inside `MergeReadyCheck`, but that would leak Git preflight details into workflow checks and force approval/Integrate to reimplement snapshot validation. Reuse `mergeApprovedCandidate()` with a dry-run flag, but that method also commits integration artifacts and performs the real delivery merge, making a read-only check harder to reason about.

### D2: Run the preflight in a disposable detached worktree

The preflight resolves `baseSha`, `candidateHeadSha`, and `mergeBaseSha`, creates a temporary worktree detached at `baseSha`, runs `git merge --squash <candidateHeadSha>`, records the result, and removes the temporary worktree. It never checks out or writes to the project base branch, never advances branch refs, and never writes into the issue worktree.

On merge failure, it reads conflict files from the temporary worktree using the existing unmerged-file mechanism before cleanup. Cleanup should be best-effort but logged; a cleanup failure should not convert a detected conflict into a pass.

**Alternatives considered:** Use `git merge-tree` or `git merge-tree --write-tree`, but behavior and conflict reporting vary across Git versions and do not exactly match the final `git merge --squash` command. Use the main project worktree and reset afterward, but that risks leaving `master` dirty and repeats the class of side effects the preflight is meant to avoid.

### D3: Make `merge-ready` a direct projection of the preflight result

`MergeReadyCheck` should call `checkSquashMergeability()` and set `status = 'pass'` only when `snapshot.canMerge === true`. The check output should be the snapshot plus compatibility fields only if needed by existing repair prompts, such as `conflictFiles` and `targetBranch`.

The older/internal `merge-readiness` check should either delegate to the same API or be removed from active use if no longer wired. It must not keep the old `conflictingFiles.length === 0` inference.

**Alternatives considered:** Keep fast-forward as an automatic pass. This is not sufficient because the required meaning is final squash mergeability, not branch topology. Keep the old output and only add fields. This preserves the misleading `cleanRebaseFeasible` concept as a decision input, so the decision source must change first.

### D4: Persist and compare mergeability snapshots at approval

When Check builds approval output, include the latest passing `merge-ready` output as `mergeReadySnapshot` alongside the review `snapshotSha`. Approval validation should compare that snapshot to current Git facts before enqueuing Integrate:

- `targetBranch` still matches the project base branch.
- `baseSha` still equals `git rev-parse <baseBranch>`.
- `candidateHeadSha` still equals `git rev-parse mo/issue-N` or the issue worktree HEAD.
- `mergeBaseSha` still equals the current merge base for the same refs.
- `canMerge` is still true.

If any field differs, return a 409 that asks the user to rerun Check instead of silently reusing stale approval evidence. The validation should fail closed when required snapshot fields are missing.

**Alternatives considered:** Rerun the merge preflight directly from the approval API and approve if it passes. That hides the fact that the approval prompt was based on stale evidence and can make the user's click approve a different candidate than the one presented. Rejecting stale evidence keeps approval bound to what the user reviewed.

### D5: Add an Integrate preflight before side effects

At the start of `IntegrateStageRunner.executeTasks()`, before `runSpecSyncStep()` and `runArchiveStep()`, validate the approved `mergeReadySnapshot` against current base/head facts. If the snapshot is absent or stale, run `checkSquashMergeability()` once and stop Integrate if it fails.

Successful refreshed evidence can be recorded as Integrate diagnostic output, but it should not mark Check approval as current. If the base/head changed after approval, Integrate should fail locally with clear evidence and instruct a Check rerun rather than advancing delivery based on an unapproved candidate.

**Alternatives considered:** Trust the approval API to catch all staleness. That leaves server restarts, direct WorkflowRun resume paths, and races between approval and Integrate uncovered. Move preflight after spec sync/archive. That catches more final-branch conflicts but still allows avoidable side effects before the first mergeability check.

### D6: Keep final merge authoritative and improve conflict extraction

`mergeApprovedCandidate()` remains the final delivery authority and still runs the real `git merge --squash <branch>`. Its failure path should gather conflict files before cleanup and include `baseSha`, `candidateHeadSha`, `mergeBaseSha` when available, `targetBranch`, `strategy`, and `conflictFiles` in the structured failure output.

This handles races after preflight and conflicts introduced by Integrate-generated artifact commits. The user-visible behavior remains a failed `integrate:merge` task, but with actionable conflict file evidence.

**Alternatives considered:** Skip the final merge if preflight passed. This would remove the only authoritative operation that actually lands the candidate and would be unsafe under concurrent base changes.

### D7: Cover the #207 regression with real Git fixtures

Add focused tests that create a temporary repository with a base branch and issue branch where `getWorktreeStatus().conflictingFiles` is empty, but `git merge --squash <issue-branch>` against current base conflicts. The test should assert that `merge-ready` fails and reports the conflicting files.

Add tests for snapshot staleness by changing the base branch after a passing preflight and verifying approval or Integrate rejects the stale snapshot. Add final merge failure coverage that verifies `mergeApprovedCandidate()` reports conflict files when a race occurs after preflight.

**Alternatives considered:** Mock `WorktreeManager` only. Mocks are useful for workflow validation, but the bug is specifically a mismatch between inferred state and real Git merge behavior, so at least one real Git regression is necessary.

## Risks / Trade-offs

- [Temporary worktree cleanup can fail] → Use unique temp paths, always attempt `git merge --abort` or `git reset --merge` where applicable, remove the worktree with `git worktree remove --force`, run `git worktree prune` opportunistically, and log cleanup failures separately from mergeability results.
- [Preflight adds Git cost to Check] → Keep the preflight scoped to one temporary worktree and one squash merge attempt; correctness is more important than the small added latency at an approval gate.
- [Integrate can create commits after preflight] → Keep the actual merge as final authority and report structured conflict files for post-preflight races or artifact-commit conflicts.
- [Approval may reject more often when base moves] → Return clear 409 messages that say the base or candidate changed and Check must be rerun, preserving user trust over convenience.
- [Existing clients may expect old output fields] → Keep harmless compatibility fields in output while making `canMerge`, SHAs, and `strategy` the decision contract.

## Migration Plan

1. Add `MergeabilitySnapshot` and `checkSquashMergeability()` to `WorktreeManager` and the workflow context interface.
2. Update `MergeReadyCheck` and any active `merge-readiness` path to use the shared preflight and persist the structured snapshot.
3. Include `mergeReadySnapshot` in Check approval output and update approval API validation to reject missing or stale snapshots.
4. Add Integrate preflight validation before spec sync/archive and ensure stale approval evidence fails before side effects.
5. Improve `mergeApprovedCandidate()` failure output to collect conflict files before cleanup and include the same merge identity fields where possible.
6. Add regression tests with real Git repositories plus workflow/API tests for stale snapshot handling.
7. Rollback strategy: revert callers to the previous `merge-ready` logic only together with disabling snapshot validation; no data migration is required because snapshots are stored as JSON check/approval output.

## Open Questions

None.
