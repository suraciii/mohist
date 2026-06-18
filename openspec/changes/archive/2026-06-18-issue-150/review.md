# Review Report

## Result: PASS

The change correctly introduces branch-stability enforcement at task boundaries
and moves publish/merge-ready onto isolated landing workspaces. All issue-150
acceptance criteria are satisfied by code paths that have meaningful unit and
end-to-end tests. The 7 changed test files plus the runner/CLI/web test suites
targeted by this change all pass (81/81 runner issue-150 tests, 30/30 web
delivery-failure tests, 39/39 C# `IssueCliTableRendererSpecs`, and 1743/1753
server tests with 10 pre-existing skips — verified during review). Pre-existing
test failures in `issue-112-regression.spec.ts` and `runner-host.spec.ts` exist
on the unchanged baseline and are unrelated to this change.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: spec drift (documentation)
  Evidence: `openspec/changes/issue-150/specs/worktree-manager/spec.md` line 33
  stated "An isolated landing workspace SHALL be materialized from the shared
  repository cache", but the implementation materializes via
  `git clone --shared` of the workflow workspace path (see
  `packages/runner/src/runtime/workspace.ts:107`), because the run branch's
  prepared commits are not in the bare cache until published. The design
  documents this decision (design.md Decision 3) but the requirement text was
  left in the original form. The MODIFIED-vs-ADDED status of the requirement
  made the wording both misleading and an archive-time risk.
  Verification: re-read the requirement text against
  `packages/runner/src/runtime/workspace.ts:77-152`. Requirement now reads
  "materialized as a `git clone --shared` of the workflow workspace (so the
  run branch's prepared commits are visible alongside the base branch)", which
  matches the implementation. Re-typecheck runner passed (`npx tsc --noEmit`).
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: traceability link drift
  Evidence: `openspec/changes/issue-150/tasks.json` line 44 referenced
  `specs/merge-delivery/spec.md#Failed delivery leaves a clean workspace on
  the run branch`, but the requirement header in
  `openspec/changes/issue-150/specs/merge-delivery/spec.md` is `Failed delivery
  leaves a clean workspace` (the on-the-run-branch strengthening is in the
  requirement body, not the header). This is the same fix applied by the
  prior self-review and re-introduced in the new tasks.json's `notes`.
  Verification: edited the `notes` string to point at the actual header and
  inline-noted the body strengthening. No JSON structural changes.
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/runner/src/actions/registry.ts:389`
  Evidence: After a successful fast-forward in `publishInLandingWorkspace`,
  the line `restoreSha = remoteHead.stdout.trim()` re-assigns the same value
  that was assigned at line 347 (which `remoteHead.stdout.trim()` already
  yielded). It is a no-op leftover from a refactor and not covered by any
  test.
  SuggestedAction: Remove the redundant reassignment.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/runner/src/runtime/executor.ts:870-890` and `WorktreeProbeError`
  Evidence: With the new branch-stability start check running before
  `enforceCleanWorktree`, `WorktreeProbeError` is only thrown from
  `readWorktreeSnapshot` (lines 688, 700), which runs only when the start check
  passes. The branch-stability check uses the same `git rev-parse` family and
  would surface a corrupted `.git` as a `branch-invariant-violation` with
  `detail: "probe failed: ..."` before reaching `readWorktreeSnapshot`. As a
  result, `worktreeProbeFailure` and the `WorktreeProbeError` catch in
  `executeOne` (line 150-151) are now unreachable through normal execution,
  and the test `executor-cleanup.spec.ts:592-629` was updated to assert
  `branch-invariant-violation` for what used to be a probe-failure failure.
  The awkward `...({ probeError, probeExitCode }) as unknown as
  Pick<DirtyWorktreeEvidence, never>` cast (line 877-880) is a side effect of
  this dead-code path.
  SuggestedAction: Remove `worktreeProbeFailure`, `WorktreeProbeError`,
  and the catch at `executor.ts:150-151`. Update or retire any test that
  still asserts the old `dirty-worktree` probe-failure shape (none currently
  do).
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/runner/src/runtime/executor.ts:573-617` test name vs. coverage
  Evidence: The test named `endBoundaryCheckRunsBeforeCleanWorktreeCheck_OrderingProof`
  (line 573) actually exercises the **start** boundary (it places the
  workspace on `master` before the task runs and asserts `boundary: "start"`).
  The end-boundary ordering is correctly covered by
  `endBoundaryCheckPrecedesCleanWorktreeCheckSoWrongBranchIsNotMisreportedAsDirty`
  (line 184-217), which leaves the action moving the branch. The misnamed test
  does not add a missing case; it duplicates the start-boundary proof with
  different fixtures.
  SuggestedAction: Rename or merge the misnamed test with the start-boundary
  suite.
  Status: follow-up

- [ID: item-6]
  Severity: follow-up
  Scope: `openspec/changes/issue-150/tasks.json` (T-004 acceptance criteria)
  Evidence: T-004 is intended to also enforce the codified branch-stable
  guarantee for `integrate:prepare`. Prepare is unchanged and now relies
  entirely on the executor's start/end boundary checks (T-004 acceptance
  criteria + design Decision 5). The existing `prepare.spec.ts` suite mocks
  `setDeliveryGitRunnerForTest` rather than driving `prepareAction` through
  the executor, so it cannot catch a regression where someone removes the
  boundary check or wires prepare through `project.path` instead of
  `workspace.path`. The self-review's follow-up item-2 already flagged the
  need for an executor-level regression assertion that prepare never emits a
  `checkout <baseBranch>` against the workflow workspace.
  SuggestedAction: Add a small executor-driven test that runs prepare through
  `WorkExecutor.execute` and asserts the workspace stays on
  `workspace.branch` before and after.
  Status: follow-up

- [ID: item-7]
  Severity: follow-up
  Scope: `packages/runner/src/runtime/executor.ts:328-366` (executeChecks)
  Evidence: As documented in `design.md` Open Question 1 and self-review
  follow-up item-3, `executeChecks` does not currently apply the
  branch-stability check. `mergeReadyAction` was made ref-safe via the
  landing workspace, so the immediate risk is gone, but the design's intent
  is to extend the check to checks once merge-ready is ref-safe. The follow-up
  is acknowledged and tracked outside this change.
  SuggestedAction: Decide whether to wrap check-type work in a follow-up
  issue and gate subsequent changes on it.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-8]
  Severity: warning
  Scope: `packages/runner/src/actions/registry.ts` merge-ready semantic
  Evidence: `mergeReadyAction` resolves `source = stringInput(context.with,
  "source") ?? "HEAD"` (line 148). The default workflow profile
  `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-default.workflow.yaml:239-241`
  calls `mohist/merge-ready` without `with.source`, so `source = "HEAD"`. After
  the landing workspace's `checkout target` step in
  `runSquashMergePreflightInLanding` (line 544), `HEAD` resolves to the target
  branch's tip, making `merge --squash --no-commit HEAD` a no-op merge. The
  pre-existing `mergeReadyAction` in the unchanged baseline had the same
  behaviour (`runSquashMergePreflight(workDir, ...)` followed by
  `checkout target` then `merge --squash --no-commit HEAD`). The new branch-
  stable preflight preserves the bug. In practice, real conflict detection
  lives in `integrate:publish` (which correctly passes
  `source: ${{ workspace.branch }}` at workflow yaml line 284) and in
  `integrate:prepare` (which runs the actual rebase), so the merge-ready
  check has been effectively a no-op for conflict detection all along.
  Because the workflow yaml still does not pass `with.source` and the change
  description does not commit to fixing it, this is preserved.
  SuggestedAction: Update the workflow yaml to pass
  `source: ${{ workspace.branch }}` for `mohist/merge-ready`, or change the
  default in `mergeReadyAction` to fall back to `workspace.branch` instead
  of `HEAD`. Out of scope for issue-150; track as a separate issue.
  Status: pre-existing

- [ID: item-9]
  Severity: info
  Scope: `packages/runner/tests/issue-112-regression.spec.ts`
  Evidence: This file imports `mergeAction` and `setMergeGitRunnerForTest`
  from `../src/actions/registry.js` (line 9). Both symbols were removed when
  issue #141 split Integrate delivery into prepare + publish. 4 tests in this
  file fail with `TypeError: setMergeGitRunnerForTest is not a function`.
  The failure reproduces on the unchanged base (`249ef136`) and is not caused
  by issue-150.
  SuggestedAction: Migrate or retire the issue-112-regression suite to the
  new prepare/publish actions.
  Status: pre-existing

- [ID: item-10]
  Severity: info
  Scope: `packages/runner/tests/runner-host.spec.ts:RunnerConnection_WhenSignalRFails_DoesNotPollAndRetriesCleanly`
  Evidence: Test fails on the unchanged base (`249ef136`) and is unrelated to
  this change. Already in the pre-existing failure list.
  SuggestedAction: Investigate in its own change.
  Status: pre-existing

- [ID: item-11]
  Severity: info
  Scope: `packages/runner/src/runtime/workspace.ts:177-203`
  Evidence: `pruneLandingWorkspaces` matches by `startsWith("safeRunId-")`,
  which is safe against prefix collisions (the trailing `-` makes
  `wr-1-` and `wr-10-` distinguishable) but uses `runId`-derived string
  sanitization only. Two different runtime identifiers that collapse to the
  same `safeRunId` (e.g. `wr/1` and `wr.1`) would prune each other's landing
  dirs. The likelihood is low and the impact is bounded to landing dirs
  (which are designed to be disposable), but it is worth documenting.
  SuggestedAction: Document the assumption in the function's docstring, or
  switch to a stricter match (e.g. include the project slug in the dir name).
  Status: pre-existing

- [ID: item-12]
  Severity: info
  Scope: `packages/runner/src/actions/registry.ts:507-532` (merge-ready cleanup-failure contract)
  Evidence: When the landing workspace cannot be disposed, the landing dir is
  left on disk and is only removed by the next `pruneLandingWorkspaces` on
  `ensure()` for the same runId. This is correct behaviour per the design
  (landing dirs are disposable and isolated), but operators should be aware
  that a stuck dispose accumulates landing dirs until the next ensure. No
  observable failure is masked.
  SuggestedAction: Add a runner-level metric or warning log when
  `disposeLandingWorkspace` returns `disposed: false`. Out of scope for
  this change.
  Status: pre-existing

- [ID: item-13]
  Severity: info
  Scope: `packages/runner/src/runtime/executor.ts:439-509` (artifact capture
  error path)
  Evidence: Unchanged from baseline. Mentioned for context only — the new
  branch-stability check preserves the artifact upload path for completed
  tasks. Pre-existing.
  Status: pre-existing

<promise>PASS</promise>