# Review Report

## Result: FAIL

## Repaired Items

_(none)_

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/runner/src/runtime/executor.ts; packages/runner/src/runtime/worktree-enforcement.ts; complexity acceptance
  Evidence: Issue 255 acceptance requires the extracted modules to return to a healthy scc range and leave the runner package complexity front ranks. The spec repeats this in `openspec/changes/issue-255/specs/runner-executor-structure/spec.md:119`-`132`, specifically saying `executor.ts` SHALL not remain in the front ranks and `branch-stability.ts` / `worktree-enforcement.ts` SHALL each leave the front ranks. The current post-build snapshot still has `packages/runner/src/runtime/executor.ts` at 451 lines and complexity 87, ranked 8th of 50 files by `scc --by-file --sort complexity packages/runner/src`. `packages/runner/src/runtime/worktree-enforcement.ts` is also still near the front at 431 lines and complexity 69, ranked 11th. `packages/runner/src/runtime/branch-stability.ts` is healthy at complexity 35, ranked 27th, but the acceptance criterion is not met for the full set. This is not repairable under the review repair policy because meeting the threshold requires either changing the accepted complexity threshold or further refactoring execution/check/recovery/worktree responsibilities. [disallowed:architectural-judgment,broad-refactoring]
  SuggestedAction: Decide the concrete scc threshold for "front ranks" and either reduce `executor.ts` and `worktree-enforcement.ts` below it, or update the accepted spec/design to state the new target explicitly. Re-run file-level scc and record the rank/complexity evidence after the change.
  Verification: `scc --by-file --sort complexity packages/runner/src` currently reports `runtime/executor.ts` complexity 87 at rank 8, `runtime/worktree-enforcement.ts` complexity 69 at rank 11, and `runtime/branch-stability.ts` complexity 35 at rank 27.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: packages/runner/src/runtime/artifact-capture.ts; packages/runner/src/runtime/set-vars.ts; packages/runner/src/runtime/check-verdict.ts; planned scope consistency
  Evidence: The proposal's impact section says this issue does not change `artifact-capture.ts`, `set-vars.ts`, `output-capture.ts`, `worktree-cleanup.ts`, `cleanup-loop.ts`, or `workspace.ts` (`openspec/changes/issue-255/proposal.md:24`). The candidate changes `packages/runner/src/runtime/artifact-capture.ts` (+169 lines), `packages/runner/src/runtime/set-vars.ts` (+31 lines), and adds `packages/runner/src/runtime/check-verdict.ts`. The behavior is covered by the passing runner tests, but the change broadens the deliverable beyond the documented extraction plan and moves complexity into `artifact-capture.ts`, which now ranks 10th by scc with complexity 69. This is not a small review repair because resolving it requires either scope/design reconciliation or moving helper ownership again. [disallowed:architectural-judgment]
  SuggestedAction: Reconcile the plan with the implementation: either amend the proposal/design/spec to explicitly include post-side-effect/check helper extraction and its complexity impact, or keep this issue limited to the two invariant modules and defer post-side-effect/helper extraction to a separate issue.
  Verification: `git diff --stat master...HEAD` shows `artifact-capture.ts`, `set-vars.ts`, and `check-verdict.ts` changed/added; `scc --by-file --sort complexity packages/runner/src` reports `runtime/artifact-capture.ts` complexity 69.
  Status: open

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: packages/runner/src/runtime/executor.ts; packages/runner/src/runtime/worktree-enforcement.ts
  Evidence: `baseContext` now exists in both `executor.ts:252` and `worktree-enforcement.ts:385`. The duplication preserves behavior today, but future changes to action context fields must be made in both places or cleanup attempts will drift from normal task execution.
  SuggestedAction: If this code changes again, consider a small shared context-builder helper or add a focused unit test that compares the cleanup context fields with normal task context fields.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: openspec/changes/issue-255/progress.txt
  Evidence: The progress artifact records stale scc evidence at `openspec/changes/issue-255/progress.txt:167`-`176` (`executor.ts` 477 lines / complexity 142 / rank 3). The current snapshot is different (`executor.ts` 451 lines / complexity 87 / rank 8). This does not affect product behavior, but it weakens traceability for the complexity acceptance criterion.
  SuggestedAction: Update the progress evidence after the complexity issue is resolved so the artifact matches the reviewed candidate snapshot.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: warning
  Scope: packages/runner/src/runtime/executor.ts recovery path
  Evidence: `executeOne` calls `tryRecovery(work, normalized)` at `packages/runner/src/runtime/executor.ts:98` before checking whether `normalized.status` is completed at `packages/runner/src/runtime/executor.ts:100`, and `tryRecovery` itself has no failed-status guard (`packages/runner/src/runtime/executor.ts:347`-`360`). That means a successful action whose JSON output matches a recovery handler can still schedule recovery. The diff shows this ordering existed before the refactor, so it is not introduced by this candidate and preserving it is consistent with the issue's no-behavior-change constraint.
  SuggestedAction: Consider a separate behavior issue to decide whether recovery should be gated to failed action results only, with regression coverage for successful matching output.
  Status: pre-existing

## Verification Summary

- `npm run typecheck -w packages/runner` passed.
- `npm test -w packages/runner` passed: 56 test files, 755 tests.
- `scc --by-file --sort complexity packages/runner/src` produced the failing complexity evidence above.
- `grep` found no `.skip` or `.only` in `packages/runner/tests/**/*.spec.ts`.

<promise>FAIL</promise>
