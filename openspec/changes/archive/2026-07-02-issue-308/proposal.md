## Why

The runner's `github-pr.ts` is the single most complex hand-written file in the repo (scc Complexity 384 / 1379 lines). Two unrelated concerns — the merge-pipeline state machine (`waitChecksAndMergePr` + `waitForPrChecks`: PR-state short-circuit, check-poll loop, `mergeStateStatus` re-confirm) and the `gh` failure-text classifier matrix (`classifyGhFailure` + five `looksLike*` matchers) — each own a large share of the branching, yet sit in one file with the three action orchestrators. Every adjustment to PR-check polling strategy or failure triage forces edits deep inside this monolith and risks tangling unrelated stages. The file is on the hot path of the standard PR workflow (real `git push` + `gh pr merge`), so the cost of an accidental cross-stage regression is high.

## What Changes

Split `packages/runner/src/actions/github-pr.ts` into collaborating modules by concern, **behavior-preserving**. Three registered actions (`create-github-pr` / `merge-github-pr` / `mark-github-pr-ready`) collapse to thin orchestration layers.

- Extract the `gh` failure-text classifier matrix — `classifyGhFailure`, `classifyPushFailure`, `looksLikeBaseMoved` / `looksLikeProtectionConflict` / `looksLikePrStateConflict` / `looksLikeAuthFailure` / `looksLikeRetrySafe` — into its own module.
- Extract the `gh` output parsers — `parsePrList`, `parsePrListWithDraft`, `parsePrView` / `parsePrViewWithDraft` (+ `parsePrViewInternal`), `extractPrNumberFromUrl`, `combinedGhOutput`, `errorMessage` — into their own module.
- Extract the check-rollup classification — `parsePrStatusCheckRollup` (+ result parser), `classifyRollupBucket`, `classifyPrChecks`, `formatFailedCheck` — into its own module.
- Extract the merge-pipeline state machine — `waitChecksAndMergePr`, `waitForPrChecks`, `mergeStateStatusFailure`, `delayWithSignal`, `runGhReadWithRetry`, `runGhPrecheck` — into its own module.
- Extract the issue-field bridge (`resolveIssueFieldValue`, `loadIssueFields`, `validateIssueFieldSource`, `requiredIssueFields`), the git-ref helpers (`resolveCurrentBranch`, `resolveBaseSha`, `openOrReusePr`, `resolvePrNumberForMerge`), and the output adapters (`buildCreateGitHubPrOutput` / `buildMergeGitHubPrOutput` / `markReadyOutput`) to focused modules (or fold into the orchestrators where they belong).
- The `setGitHubPrGitRunnerForTest` / `setGitHubPrGhRunnerForTest` / `setGitHubPrChecksTimingForTest` / `setGitHubPrTransientRetryForTest` injection stubs migrate to the module that consumes each knob, preserving the existing mutable-`let` injection pattern.
- **Strengthen coverage**: add direct unit tests for each `looksLike*` matcher's phrase set (today only `looksLikeRetrySafe` has dense ~15-phrase coverage; the others are sparsely exercised through the orchestrators).

**Unchanged (Non-Goals):** the three actions' execution semantics, fault-tolerance/retry strategy, and git/gh side-effect ordering; the action IDs; the output JSON field shape; the `GitHubPrErrorCode` value set; the exact git/gh command strings and their sequence; the step-recorder step names; and the references from the server's `mohist/github-pr` workflow profile. **BREAKING**: none — this is an internal module reorganization with an identical external surface.

## Capabilities

### New Capabilities

- _(none — this is a behavior-preserving internal refactor. Module decomposition of a single runner source file is an implementation detail, not a product/system capability.)_

### Modified Capabilities

- _(none — every existing requirement that governs these actions lives in `pr-first-workflow` (draft-PR creation as the final plan task, `mark-github-pr-ready`, exactly-one `merge-github-pr` integrate delivery, PR-identity projection into `vars.github.pr.*`, checks-as-internal-precondition waiting, `gh pr merge --squash`, post-merge `MERGED` re-confirm, the `pr-checks-failed` / `base-moved` error-code contracts and their declared recovery cases). All of those are explicitly preserved by this change's Non-Goals, so no spec-level requirement changes.)_

## Impact

- **Runner** (`packages/runner/src/actions/`): `github-pr.ts` is decomposed into several focused modules (e.g. `github-pr-classify.ts`, `github-pr-parse.ts`, `github-pr-checks.ts`, `github-pr-merge.ts`, plus issue-field / git-ref / output helpers); the three `*Action` entry points remain the public surface and shrink to orchestration. No new action is registered; no action is removed.
- **Test injection**: the four `setGitHubPr*ForTest` entry points keep their names and semantics but are re-exported from (or relocated to) the module that owns each knob, so the existing specs continue to inject unchanged. Import sites in the specs are updated to the new module paths.
- **Tests** (`packages/runner/tests/`): `create-github-pr.spec.ts` / `merge-github-pr.spec.ts` / `mark-github-pr-ready.spec.ts` (~30 cases, incl. exact-command-string assertions) MUST pass unmodified in behavior; only import paths change where they reference relocated helpers. New direct unit tests are added for the `looksLike*` phrase matrices.
- **Server / Web / CLI**: no changes — no action ID, output contract, error-code value, or workflow-profile reference is touched.
- **Dependencies / persisted data**: none. No migration, no API contract change, no new external dependency.
- **Risk**: the refactor touches real `git push` and `gh pr merge` on the standard PR workflow's hot path, so it is gated on the three existing spec files (exact command-string + step-name + output-JSON assertions) plus the new classifier unit tests passing.
