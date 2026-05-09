## Why

Check-stage approval is currently allowed to observe contradictory AI review truth after auto-fix: stale failed check state can survive while refreshed review artifacts report PASS, and fix changes may remain uncommitted. This change is needed so users only approve a converged snapshot where the persisted AI review verdict, review artifacts, and code under approval all describe the same final state.

## What Changes

- Treat the post-auto-fix re-review as the authoritative AI review result for the current check cycle, replacing any earlier failed AI review state before approval can be requested.
- Require re-review to regenerate the review report from the current fixed code snapshot instead of reusing an existing `review.md` as the source of truth.
- Bind the authoritative AI review verdict and approval output to the current code snapshot so `issue show`, API responses, check-suite state, and approval details cannot disagree about PASS/FAIL.
- Gate user approval on truth convergence: latest AI review result, latest review artifacts, self-check artifacts, and the current code snapshot must be consistent.
- Prevent unhandled auto-fix worktree changes from entering approval; Mohist must either include them in the approved snapshot or block approval with a clear reason.
- Preserve the latest re-review FAIL result and report when re-review fails, and do not request ordinary user approval from a failed or unconverged check cycle.
- Add regression coverage for AI review FAIL → auto-fix → regenerated re-review PASS → persisted PASS → approval requested, stale `review.md` reuse, uncommitted auto-fix handling, and re-review FAIL behavior.

## Capabilities

### New Capabilities

- check-suite

### Modified Capabilities

- workflow-engine
- pipeline-model
- http-api

## Impact

- Affects `packages/cli/src/workflow/check-stage-runner.ts`, where `review.md` and `review-self-check.md` are generated or skipped and where check-stage auto-fix must force a fresh review pass after code changes.
- Affects `packages/cli/src/workflow/base-stage-runner.ts`, where failed checks are fixed and rechecked, check results are persisted, and check-stage approval output is built from the AI review result.
- Affects `packages/cli/src/workflow/checks/ai-review-check.ts`, which currently parses existing `review.md` and must not let stale artifacts stand in for a regenerated re-review of the current snapshot.
- Affects `packages/cli/src/workflow/review-fix-task.ts` and related check-stage repair flow because auto-fix code changes must be detected, committed or blocked, and associated with the subsequent authoritative review.
- Affects persisted check state in `packages/cli/src/db/stage-execution-repo.ts`, `packages/cli/src/db/check-suite-repo.ts`, and related types so the current AI review result replaces stale failed truth and remains tied to the code snapshot.
- Affects `GET /api/issues/:number`, `GET /api/issues/:number/check-suite`, and approval-related API behavior in `packages/cli/src/api/issues.ts` so clients see the latest authoritative verdict and cannot approve an unconverged snapshot.
- Affects CLI issue detail rendering in `packages/cli/src/cli/commands/issue.ts` because displayed gate status and approval output must agree.
- No new external dependencies are expected.
