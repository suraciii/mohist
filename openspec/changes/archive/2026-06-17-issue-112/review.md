# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: dead code (`packages/runner/src/actions/registry.ts:582`)
  Evidence: After the per-phase failure-helper extraction (`fetchFailure`/`rebaseConflictFailure`/`landingValidationFailure`/`pushFailure`), the old inline `return mergeFailure({...})` blocks at the top of the loop are gone, but the helper `mergeFailure` at the bottom of the file remains in active use. Confirmed no dead branches in the new flow.
  Verification: `npx tsc -p tsconfig.json --noEmit` exits 0; `npx vitest run tests/merge.spec.ts` passes (14 tests).
  Status: not-applicable

- [ID: item-2]
  Severity: info
  Scope: dead code (`packages/runner/src/actions/registry.ts:1083`)
  Evidence: `randomUUID` is still used by `scriptAction` (line 98). `trim` is still used by `scriptAction` (line 107). No dead imports remain after the helper extraction.
  Verification: `grep -nE '\\btrim\\(|\\brandomUUID\\(' packages/runner/src/actions/registry.ts` shows real usages at lines 98 and 107.
  Status: not-applicable

- [ID: item-3]
  Severity: info
  Scope: small test cleanup
  Evidence: The two duplicated `case "checkout mo/issue-112":` rows introduced during repair (`tests/merge.spec.ts:172-173` and `tests/issue-112-regression.spec.ts:240-242`) were left in place after the duplicate-removal edit. Verified they were collapsed: re-grep confirms exactly one `case "checkout mo/issue-112":` per `setMergeGitRunnerForTest` block.
  Verification: `grep -c 'case "checkout mo/issue-112":' tests/merge.spec.ts tests/issue-112-regression.spec.ts` → 1 per test setup block.
  Status: not-applicable

## Blocking Items

None.

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: spec wording / tests vs. spec — `openspec/changes/issue-112/specs/merge-delivery/spec.md:131-135`
  Evidence: Scenario "Push retry bound defaults to five" reads "the default Integrate workflow runs → `mohist/merge` SHALL be configured with `maxPushRetry: 5`". The default Integrate workflow (`packages/server/.../mohist-default.workflow.yaml:251-264`) does not set `maxPushRetry`; the action's own `DEFAULT_MAX_PUSH_RETRY = 5` (`registry.ts:28`) supplies the value. The behavior matches the spec but the scenario wording suggests the workflow sets it explicitly. Either add `maxPushRetry: 5` to the workflow yaml to match the scenario, or soften the scenario to "the action's default bound is 5". The current `tasks.json:42` ("default workflow YAML has push:true and remote:origin") does not include `maxPushRetry`, suggesting the workflow sets nothing extra; therefore the spec scenario is misleading.
  SuggestedAction: Soften the spec scenario to "the action's default bound for `maxPushRetry` is 5" so it does not promise an explicit workflow override that the implementation does not make.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: misleading `lastRemoteSha` semantics — `packages/runner/src/actions/registry.ts:208, 399, 451, 463, 481, 497, 522`
  Evidence: The `lastRemoteSha` evidence field is set to `baseSha` (the local `rev-parse <remote>/<target>` result, i.e. the just-fetched remote-tracking ref) on each push attempt, not the actual remote SHA. The field is only updated from `ls-remote` when a push is rejected (lines 459-464) and only persists into the next iteration; on the success path it reports the second `baseSha`, not the rejected-discovery SHA from the previous iteration. The test `PushRejectedAsRemoteAdvanced_RefetchesRebasesRegeneratesAndRetries` (line 462-466) passes only because both intermediate `baseSha` values and the `ls-remote` discovery agree (`new-remote-sha` is not used in the success path). For an operator reading the delivery facts, "last remote SHA" most naturally means "the remote SHA the runner last saw", which here is whatever the runner last fetched, not whatever the runner last observed via `ls-remote`. The two can diverge; for instance, if a subsequent fetch returns a different commit than the `ls-remote` of the previous iteration, the reported `lastRemoteSha` no longer reflects any actual remote observation.
  SuggestedAction: Either rename the field to `lastFetchedBaseSha` to match the implementation, or compute the field from `ls-remote` after every fetch (so the reported value is "the remote SHA at the time of the push that just succeeded"). Document the chosen semantic in the spec.
  Status: follow-up

- [ID: item-6]
  Severity: follow-up
  Scope: phase mislabeling — `packages/runner/src/actions/registry.ts:257-269` (rebaseSourceOnto checkout failure)
  Evidence: When the defensive `git checkout <source>` in `rebaseSourceOnto` fails, the action reports `phase: "fetch"` via `fetchFailure`. A checkout failure is not a fetch failure; it happens after a successful fetch (because the rebase step was reached) and is a failure of the source-branch guard. The error message is correctly informative ("Could not check out or rebase source branch"), but the phase field misclassifies the failure for downstream tooling that keys on phase to surface remediation. The merge-delivery spec only enumerates `source-cleanup`, `fetch`, `rebase-conflict`, `landing-validation`, `push`, so this failure mode has no good phase to land in.
  SuggestedAction: Either introduce a `source-checkout` phase in the spec and use it here, or accept the current "fetch" phase as the closest match and note in a code comment that checkout failures are labeled "fetch" for legacy reasons.
  Status: follow-up

- [ID: item-7]
  Severity: follow-up
  Scope: probe-error type cast — `packages/runner/src/runtime/executor.ts:575-595` (`worktreeProbeFailure`)
  Evidence: `worktreeProbeFailure` builds a `DirtyWorktreeEvidence` and then merges in `probeError` and `probeExitCode` via an `as unknown as Pick<DirtyWorktreeEvidence, never>` cast (`executor.ts:582-586`). The cast is a deliberate lie about the evidence shape so that the JSON output preserves the probe-error fields under the same `kind: "dirty-worktree"` evidence object. The unit test `worktreeProbeFailure_FailsTaskWithStructuredEvidence` (`tests/executor-cleanup.spec.ts:454-487`) only asserts the standard `kind`/`staged`/`unstaged`/`untracked`/`cleanupAttempts` fields, so the extra `probeError`/`probeExitCode` fields are written but never tested. If a future change reads the evidence and the cast is no longer needed, the fields will silently disappear.
  SuggestedAction: Add a separate `DirtyWorktreeProbeEvidence` type that explicitly extends `DirtyWorktreeEvidence` with `probeError`/`probeExitCode`, and add a test that asserts both fields are present in the JSON output. Drop the `as unknown as Pick<...>` cast.
  Status: follow-up

- [ID: item-8]
  Severity: follow-up
  Scope: `isRemoteAdvancedRejection` matcher coverage — `packages/runner/src/actions/registry.ts:962-972`
  Evidence: The matcher now requires "non-fast-forward" or "updates were rejected" (`registry.ts:971`). It deliberately drops the older "fetch first" phrasing. Git versions before 2.10 (mid-2017) emit `[rejected] ... (fetch first)` for non-fast-forward conditions; modern git (2.10+) emits `[rejected] ... (non-fast-forward)`. For a runner executing against a remote that has not yet been updated, or for a custom git server (e.g. Gerrit, Bitbucket proxies) that returns a different phrasing, the merge action will fall through to the generic "Fast-forward push to 'origin/master' failed" path and exhaust `maxPushRetry` after `maxPushRetry` genuine non-race failures. There is no test that exercises the "fetch first" phrasing, so the regression risk is silent.
  SuggestedAction: Add a defensive substring match for "fetch first" only when "non-fast-forward" or "updates were rejected" is also absent, and document the heuristic in a comment. Or add a per-remote override knob. (Cannot be repaired in this review — borderline product-behavior change.)
  Status: follow-up

- [ID: item-9]
  Severity: follow-up
  Scope: spec coverage — `openspec/changes/issue-112/specs/merge-delivery/spec.md:131-140`
  Evidence: The new spec scenarios "Push retry bound defaults to five" and "Push retry bound is overridable" describe the action's default and override mechanism but never assert the resulting `pushRetryAttempts` count in the evidence. The unit test `PushRejectedRetryExhausted_FailsWithPhasePush_LastRemoteShaRecorded` (line 486) does assert `pushRetryAttempts: 1` for `maxPushRetry: 1`, but the spec does not lock that contract. A future change that quietly drops the counter would not break a spec scenario.
  SuggestedAction: Add a scenario to the spec asserting "the merge action evidence SHALL record the number of push attempts consumed in `pushRetryAttempts`" with a value bound.
  Status: follow-up

- [ID: item-10]
  Severity: follow-up
  Scope: test coverage gap — `tests/merge.spec.ts`
  Evidence: The merge test suite covers the success path, fetch failure, rebase conflict (resolve and exhaust), landing validation (parent mismatch, in-progress merge), source-cleanup dirty, push skipped, remote-advanced retry success, push-retry exhausted, remote-ref verify fail, remote-ref mismatch, missing target, and the new long-source-history body cap. It does **not** cover:
  - `rebaseSourceOnto` defensive `git checkout <source>` failure (the new behavior added in this change). If the source branch was deleted between worktree creation and merge, the merge action's `phase: "fetch"` path is the only coverage.
  - `validateLanding` parent mismatch with `rebasedSha` already known (i.e. the case where the post-rebase HEAD differs from the expected base).
  - Push attempted when `remote: ""` (the action defaults to "origin" but the workflow can override; the test does not exercise the override path).
  - The `strategy: "merge"` (non-squash) branch path (line 723 in registry.ts). The spec does not mention this branch and the new tests do not cover it.
  SuggestedAction: Add focused tests for the above gaps. The squash-merge path is the only one Mohist actually emits, so the non-squash path can either be deleted (rejected) or tested (lower priority).
  Status: follow-up

- [ID: item-11]
  Severity: follow-up
  Scope: `MAX_LANDING_BODY_CHARS` inconsistency — `packages/runner/src/actions/registry.ts:877-887`
  Evidence: `capCommitBody` declares `MAX_LANDING_BODY_CHARS = 20_000` (line 878) but the test `LongSourceHistory_BodyIsCappedTo50BulletLinesWithTruncationMarker` (line 717) constructs 200 lines of ~17-char bullets (≈ 3,400 chars). Both checks (`lines.length <= 50 && body.length <= 20_000`) would pass for that body. The 20,000-char guard is therefore dead — the 50-line cap will always fire first when the line count is high. There is no test for a short-line body that exceeds 20,000 chars.
  SuggestedAction: Either remove the unused 20,000-char guard (preferred — it is unreachable in practice) or add a test that constructs a single long line and asserts the char-cap path triggers.
  Status: follow-up

- [ID: item-12]
  Severity: follow-up
  Scope: workflow ordering dependency — `packages/runner/src/actions/registry.ts:649-670` (rebaseSourceOnto)
  Evidence: The defensive `git checkout <source>` is run before the rebase. The default workflow places `integrate:merge` after `integrate:spec-sync` and `integrate:archive-change`. The archive step renames the change directory and does not touch the source branch, so the worktree is still on `mo/issue-N` at merge start. However, if a future workflow runs a non-archive `integrate:spec-sync` step that also touches the working tree, the new defensive checkout is the only thing that prevents the merge from rebase-ing the wrong branch. The new helper does not handle the case where the source branch was deleted from `refs/heads/` (e.g. after archive) — the checkout will fail with a less specific error and label the failure as `phase: "fetch"`. There is no test for this path.
  SuggestedAction: Add a unit test for the `rebaseSourceOnto` checkout-failure path that asserts the failure mode (currently a `phase: "fetch"` return with the checkout stderr in the message).
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-13]
  Severity: warning
  Scope: `packages/runner/tests/acp-agent.spec.ts`
  Evidence: Running the full runner suite shows 39 pre-existing `acp-agent.spec.ts` failures with `TypeError: context.serverConnection.openWorkflowAgentSession is not a function` and `context.serverConnection.getWorkflowAgentSession is not a function`. The same test file fails identically when reverted to the pre-T-001 commit (`76e47c95^`), so these are not caused by this change. They block the full-suite `npm test` from green but are unrelated to issue #112.
  SuggestedAction: Fix the test mock's `serverConnection` shape in a follow-up issue. The interface changed in earlier work (the `acp-agent.ts` code now expects `openWorkflowAgentSession` / `getWorkflowAgentSession` on the connection) but the test stubs were not updated.
  Status: pre-existing

- [ID: item-14]
  Severity: info
  Scope: `packages/runner/src/actions/registry.ts:147-158` (`mergeReadyAction`)
  Evidence: `mergeReadyAction` is still registered as `mohist/merge-ready` and is not exercised by issue #112. The implementation is unchanged from before the issue. Not in scope.
  SuggestedAction: None.
  Status: out-of-scope

- [ID: item-15]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-default.workflow.yaml`
  Evidence: The workflow YAML now adds `push: true` and `remote: origin` to `integrate:merge` and has no `integrate:push` task. The same `master` branch name is used as the `baseBranch` default elsewhere in the workflow. Not a regression.
  SuggestedAction: None.
  Status: out-of-scope

- [ID: item-16]
  Severity: info
  Scope: `packages/runner/src/actions/openspec.ts:110`
  Evidence: `git commit -m "..." -- specs/` (line 110) commits only the staged `specs/` files. The trailing `-- specs/` is redundant because `git add specs/` already limited the index to that path, and because the commit would only commit the already-staged set anyway. Not a bug.
  SuggestedAction: None (or remove the redundant `-- specs/` to simplify the call).
  Status: out-of-scope

- [ID: item-17]
  Severity: info
  Scope: `packages/runner/src/runtime/workspace.ts:88-101` (worktree setup)
  Evidence: The merge action's defensive `git checkout <source>` (`registry.ts:658`) is correct for the default `WorkspaceManager.ensureIssueWorktree` path which creates a worktree on the `mo/issue-N` branch. There is no test that exercises a workflow whose workspace manager puts the worktree on a different ref. Not a regression.
  SuggestedAction: None.
  Status: out-of-scope

<promise>PASS</promise>
