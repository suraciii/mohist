## Context

The check stage currently treats `AiReviewCheck` as a read-only parser of `review.md`. That works for a first review pass, but it breaks after `fix-review-findings` changes code: the recheck can read a stale `review.md`, append a newer result after an older failed result, and let approval output choose the wrong entry. The result is split truth across stage execution check results, CheckSuite state, review artifacts, approval state, and the worktree.

The implementation already has the pieces needed for convergence: `CheckStageRunner` generates `review.md` and `review-self-check.md`, `BaseStageRunner` owns fix-and-recheck orchestration and approval state, `StageExecutionRepo` persists check results, `CheckSuiteRepo` persists snapshot-bound check state, and `WorktreeManager`/git commands can inspect and commit issue worktree changes. This design adds an explicit convergence step before check-stage approval rather than moving retry policy to a new workflow model.

## Goals / Non-Goals

**Goals:**

- Make the latest re-review result replace stale AI review truth for the current check cycle.
- Ensure re-review regenerates `review.md` and `review-self-check.md` from the current fixed worktree rather than parsing an old artifact.
- Bind the authoritative AI review result and approval output to the current HEAD snapshot.
- Prevent approval when auto-fix or review artifact changes remain uncommitted or cannot be committed.
- Keep the latest re-review FAIL visible and prevent ordinary approval on failed or unconverged truth.
- Keep the implementation local to check-stage orchestration, check persistence, and approval gating.

**Non-Goals:**

- Do not define a full retry, recovery, or blocked-state strategy matrix.
- Do not redesign all stage execution history or the CheckSuite schema.
- Do not make `AiReviewCheck` responsible for spawning reviewer agents; checks remain read-only validators.
- Do not change merge queue semantics beyond ensuring check approval starts from a converged snapshot.

## Decisions

### D1: Add a check-stage review convergence boundary

After `fix-review-findings` completes, `CheckStageRunner` should run a dedicated convergence flow before `AiReviewCheck` is considered authoritative again:

1. Detect whether the fix task changed the worktree.
2. Invalidate the current review checkpoint entries and existing `review.md` / `review-self-check.md` truth for this check cycle.
3. Regenerate `review.md` and `review-self-check.md` by running the existing review artifact tasks against the current worktree.
4. Run `AiReviewCheck` against the regenerated artifact.
5. Persist that result as the single current AI review truth.
6. Converge the worktree to a clean committed snapshot or fail before approval.

This keeps the deep behavior in the stage runner, where task execution, artifact generation, check ordering, and approval are already coordinated. `AiReviewCheck` stays a small parser and validator.

**Alternatives considered:** Make `AiReviewCheck` regenerate `review.md` when it detects a stale artifact. That would hide a code-changing, agent-spawning task behind a check interface and violate the existing task/check boundary.

### D2: Replace current AI review truth instead of appending contradictory final states

When re-review runs after a fix, persistence should expose one current `ai-review` result. The stage execution history may still retain task history and prior attempts, but APIs and approval must use the latest authoritative result, not the first matching `ai-review` entry.

Implementation should avoid `allResults.find(r => r.name === 'ai-review')` for approval because that picks the stale first failure. Use a single helper such as `getLatestCheckResult(results, 'ai-review')`, and when updating durable check state (`StageExecutionRepo.check_results` and `CheckSuiteRepo.checks['ai-review']`), overwrite the current state with the latest result. If detailed attempt history is needed, store it under an explicit non-authoritative attempt field rather than as competing final results.

**Alternatives considered:** Keep appending every recheck result and teach every caller to pick the last entry. This is error-prone because stale reads can reappear anywhere that uses `find`, and it preserves contradictory final-looking data in API responses.

### D3: Regenerate review artifacts by invalidating only review-stage checkpoints for check

The stale artifact failure path comes from checkpoint/artifact skip behavior: `executeTasks()` can skip `review` when `review.md` already exists. After `fix-review-findings` changes code, the runner should clear the check-stage checkpoint steps for `review` and `review-self-check` or provide a `forceReviewRegeneration` option that bypasses those skips for the next review pass.

The design should not blindly delete unrelated files. It should either overwrite `review.md` and `review-self-check.md` through the existing artifact prompts or remove only those two known artifacts immediately before regeneration so a missing write is detected by the existing artifact verification path.

**Alternatives considered:** Always regenerate review artifacts on every check-stage run. This is simpler but loses useful resume behavior and increases review cost for runs where the code snapshot has not changed.

### D4: Commit the converged check snapshot before approval, block if commit fails

Before `UserApprovalCheck` can request approval for check stage, the runner should verify that the worktree is clean and that HEAD matches the snapshot recorded with the authoritative AI review result. If review auto-fix or artifact regeneration leaves changes, Mohist should create a normal non-interactive check convergence commit that includes the code changes and review artifacts, then set the authoritative snapshot SHA to the new HEAD.

If commit creation fails, or if hooks modify files and the worktree remains dirty, the check stage must fail or pause with a clear convergence error and must not enter ordinary approval. This chooses the "auto-commit or block" requirement as: auto-commit when possible, block when not possible.

**Alternatives considered:** Allow dirty worktrees into approval and rely on merge/integration to commit artifacts later. That preserves current behavior but is the root of the user confusion: the user cannot know which uncommitted code was reviewed or approved.

### D5: Store snapshot metadata with AI review and approval output

The authoritative AI review output should include enough metadata to verify convergence:

- `verdict`
- `reviewReport`
- `snapshotSha`
- `reviewArtifactPath`
- `selfCheckArtifactPath`
- optional `convergedAt`

`CheckSuite.snapshotSha`, `CheckSuite.checks['ai-review'].output.snapshotSha`, and `approvalState.output.snapshotSha` should match. `GET /api/issues/:number`, `GET /api/issues/:number/check-suite`, and CLI issue detail should render the latest persisted AI review state from this same source.

**Alternatives considered:** Infer the snapshot from current HEAD at display time. That is unsafe because HEAD can change after the review; the stored verdict must say which snapshot it reviewed.

### D6: Approval validation remains a final guard

Even after the runner converges truth before requesting approval, the approve endpoint should validate the check approval state before advancing to Integrate. For check-stage approval it should confirm:

- approval state is awaiting for `Stage.Check`
- active CheckSuite is awaiting approval or passed according to existing status semantics
- latest AI review verdict is PASS
- approval snapshot SHA equals active CheckSuite snapshot SHA
- current worktree HEAD equals approval snapshot SHA
- worktree has no uncommitted changes

On mismatch, the endpoint should reject the approval attempt with a clear response or enqueue a check rerun if the existing recovery pattern supports that path. It must not mark the issue approved and advance to Integrate from inconsistent truth.

**Alternatives considered:** Trust the approval state once it is written. This leaves restart, manual edit, and concurrent worker cases vulnerable to approving a different snapshot from the reviewed one.

## Risks / Trade-offs

- [Risk] Auto-committing review artifacts may create extra commits in issue branches. → Use a clear deterministic message and only commit when needed to make the approved snapshot clean and reviewable.
- [Risk] Git hooks may fail or modify files during the convergence commit. → Treat commit failure or remaining dirty status as a convergence failure and block approval with the git error summary.
- [Risk] Clearing review checkpoints incorrectly could cause unnecessary re-review. → Scope invalidation to `review` and `review-self-check` only after a fix task changes the worktree.
- [Risk] Multiple persisted stores can drift again. → Centralize AI review state updates behind one helper that updates stage execution results, CheckSuite state, and approval output from the same `AuthoritativeAiReviewResult` object.
- [Risk] Current code may not have a fully wired CheckSuite in all execution paths. → Keep StageExecution as the minimum durable source for CLI/API display, and update CheckSuite when present.

## Migration Plan

1. Add small git helpers for check convergence: read HEAD SHA, detect porcelain status, create a check convergence commit, and verify clean status.
2. Add check-stage review regeneration support that can bypass or clear `review` and `review-self-check` checkpoint skips after `fix-review-findings` changes code.
3. Change fix-and-recheck handling for `ai-review` so a successful fix regenerates artifacts before running `AiReviewCheck` again.
4. Replace stale AI review result selection with latest-authoritative selection, and persist the result with snapshot metadata to StageExecution and CheckSuite.
5. Add the pre-approval convergence guard that commits pending check changes or blocks approval with an explicit error result.
6. Harden the check approval API guard so approval cannot advance when snapshot, verdict, or worktree cleanliness no longer matches.
7. Add regression tests for PASS after auto-fix re-review, stale artifact non-reuse, commit failure blocking approval, and re-review FAIL preserving latest FAIL without approval.
8. Rollback by disabling the convergence commit/regeneration path and returning to the prior check-stage fix-and-recheck flow; persisted metadata is additive and can be ignored by older code.

## Open Questions

- Should the convergence commit use a dedicated author distinct from timeout WIP commits, or the default repository author? The implementation can start with the existing git environment and only introduce a dedicated author if auditability requires it.
