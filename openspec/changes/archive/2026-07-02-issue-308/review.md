# Review Report

## Result: PASS

## Repaired Items

- [ID: item-0]
  Severity: info
  Scope: `packages/runner/tests/github-pr-classify.spec.ts:351` — type narrowing
  Evidence: The 5-way precedence test iterates `GitHubPrErrorCode[]` (7 possible values) over a 5-element subset. The `phrase` and `loser` IIFE switch statements lack a `default` case, making their inferred return types `string | undefined`. `classifyGhFailure` expects `string`, so TypeScript flags `Argument of type 'string | undefined' is not assignable to parameter of type 'string'`. At runtime the switch is exhaustive for the iteration set, so the existing tests pass, but the LSP diagnostic is correct.
  Verification: Added `as string` type assertions to `phrase` and `loser` at the `classifyGhFailure` call site. `npm test -w packages/runner -- github-pr-classify` — 38/38 pass.
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/runner/src/actions/create-github-pr.ts:118-122`, `merge-github-pr.ts:83-87`, `mark-github-pr-ready.ts:122-126`
  Evidence: `createRecorder` (3 lines) is duplicated identically in all three orchestrator modules. The progress.txt acknowledges this as deliberate ("Not worth the extra module / DRYing into a shared helper would require moving it to a 4th new file"). The duplication is harmless at this scale.
  SuggestedAction: If a fourth action or a shared recorder concern emerges later, extract `createRecorder` to a tiny `github-pr-recorder.ts` leaf. No action needed now.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/runner/src/actions/github-pr-checks.ts`
  Evidence: `parsePrStatusCheckRollupResult` was originally module-private in the monolith but is now exported from `github-pr-checks.ts` to allow `github-pr-merge.ts` to import it directly. The barrel does NOT re-export it, so the public surface is clean, but the function is technically available for any consumer importing the checks module. Only `github-pr-merge.ts` actually imports it today.
  SuggestedAction: Optionally rename to `_parsePrStatusCheckRollupResult` to signal "internal" intent, or add a file-level `@internal` JSDoc comment. Low priority.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: `packages/runner/tests/github-pr-classify.spec.ts:330-353`
  Evidence: The 5-way `classifyGhFailure` precedence sweep uses an IIFE-per-case pattern inside a `for…of` loop to compute both the winning phrase and a "loser" text containing all lower-priority markers. This is correct but dense; a future maintainer unfamiliar with the pattern may struggle to extend it (e.g., adding a 6th bucket).
  SuggestedAction: If a 6th classifier bucket is added in a future issue, rewrite the table-driven loop as a plain array of objects with explicit `winningPhrase` / `loserText` fields. No action needed now.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/runner/tests/github-pr-runtime.spec.ts`, `packages/runner/tests/github-pr-classify.spec.ts`
  Evidence: The two new test files import from `../src/actions/github-pr.js` (the barrel) for setters and directly from `../src/actions/github-pr-classify.js` / `github-pr-runtime.js` for the functions under test. This is a mixed pattern — the classifier spec imports the matchers directly from the classify module (fine, it is testing that module) but the runtime spec imports getters directly from runtime while importing setters from the barrel. This is intentional (setters must be accessed via the barrel to test the barrel-transparency contract; getters are not re-exported from the barrel).
  SuggestedAction: No change — mixed import paths are correct and verified by the "barrel re-export from github-pr.js" test block. Only re-examine if the barrel is ever collapsed.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/runner/src/actions/github-pr-parse.ts`, `packages/runner/src/actions/github-pr-checks.ts`, `packages/runner/src/actions/github-pr-issue-fields.ts`
  Evidence: These three modules have no direct unit tests. Their behavior is exercised indirectly through the three action specs (`create-github-pr.spec.ts` / `merge-github-pr.spec.ts` / `mark-github-pr-ready.spec.ts`). The design D5 ("Optional strengthening") explicitly deferred direct unit tests for parse and checks to follow-up, stating they would be added "if time permits." The issue acceptance criteria only require direct tests for the classifier (`looksLike*` matchers), which were delivered in T-002.
  SuggestedAction: Add unit specs for `github-pr-parse.ts` (JSON parse edge cases, `extractPrNumberFromUrl`, `combinedGhOutput`, `errorMessage`) and `github-pr-checks.ts` (`classifyRollupBucket`, `classifyPrChecks`, `formatFailedCheck`) in a follow-up issue. These are pure functions that are cheap to test directly.
  Status: follow-up

## Pre-existing or Out-of-scope Items

(none)

## Evidence Summary

### Acceptance Criteria Verification

| Criterion | Evidence | Status |
|---|---|---|
| AC1 — gh failure-text classifier extracted as independent module, `looksLike*` direct tests added | `packages/runner/src/actions/github-pr-classify.ts` (76 lines, 7 exports). `packages/runner/tests/github-pr-classify.spec.ts` (398 lines, 38 tests) exhaustively covers every phrase for all 5 matchers + `classifyGhFailure` precedence + `classifyPushFailure` delegation. | PASS |
| AC2 — gh output parsers, check-rollup classification, issue-field parsing, output adapters extracted into independent modules | `github-pr-parse.ts` (98 lines), `github-pr-checks.ts` (91 lines), `github-pr-issue-fields.ts` (72 lines). Output adapters (`buildCreateGitHubPrOutput` / `buildMergeGitHubPrOutput` / `markReadyOutput`) live in their respective orchestrator modules. | PASS |
| AC3 — merge-pipeline state machine extracted; three action entries collapsed to thin orchestrators | `github-pr-merge.ts` (462 lines) owns the full state machine. The three orchestrators — `create-github-pr.ts` (256 lines), `merge-github-pr.ts` (148 lines), `mark-github-pr-ready.ts` (139 lines) — are thin orchestration layers that call into the focused modules. `github-pr.ts` (45 lines) is a pure re-export barrel. | PASS |
| AC4 — action IDs, output JSON fields, `GitHubPrErrorCode` values, git/gh command strings and sequence, step recorder names all unchanged | `github-pr-types.ts` — 7 `GitHubPrErrorCode` values (lines 1-8) + 3 `*Output` interfaces with byte-identical property order (lines 17-59). `registry.ts:9-13` imports the 3 actions from `./github-pr.js` unchanged. Step names verified across all three orchestrators + merge module: `gh-precheck`, `git-push`, `gh-pr-list`, `gh-pr-create`, `gh-pr-checks`, `gh-pr-merge`, `gh-pr-view-confirm`, `gh-pr-view`, `gh-pr-edit`, `gh-pr-ready`, `git-fetch-base`, `git-rev-parse-base`, `rev-parse-HEAD`, `git-source-anchor`. All preserved verbatim. | PASS |
| AC5 — `create-github-pr.spec.ts` / `merge-github-pr.spec.ts` / `mark-github-pr-ready.spec.ts` all pass | `npm test -w packages/runner` — 58 files, 805 tests pass. The three action specs at 8/21/9 tests pass unchanged, including exact command-string, step-name, and output-JSON assertions. | PASS |
| AC6 — test injection stubs migrated correctly; specs can inject as before | `setGitHubPrGitRunnerForTest` / `setGitHubPrGhRunnerForTest` in `github-pr-runtime.ts`; `setGitHubPrChecksTimingForTest` / `setGitHubPrTransientRetryForTest` in `github-pr-merge.ts`. All four re-exported from the barrel (`github-pr.ts:5-12`). The three action specs import them from `../src/actions/github-pr.js` with zero import-line churn (git diff empty). | PASS |

### Additional Verification

- `npm run typecheck -w packages/runner` exits 0 — no import cycles, strict DAG clean.
- `npm run build -w packages/runner` succeeds.
- `git diff packages/runner/tests/` — empty (zero spec file edited).
- `git diff packages/runner/src/actions/registry.ts` — empty (registry import unchanged).
- 11 modules total per design D1: `github-pr-types` / `github-pr-classify` / `github-pr-parse` / `github-pr-checks` / `github-pr-runtime` / `github-pr-merge` / `github-pr-issue-fields` / `create-github-pr` / `merge-github-pr` / `mark-github-pr-ready` / `github-pr` (barrel). Monolith reduced from 1379 lines to a 45-line barrel.
- `github-pr-runtime.ts` uses getter functions (`getGitHubPrGit()` / `getGitHubPrGh()`) as mandated by design D2 — live reads prevent stale-bindings after setter reset. Verified by `github-pr-runtime.spec.ts`:12 (12 tests).
- New `github-pr-classify.spec.ts` direct coverage: 38 tests covering 11 + 8 + 6 + 8 + 20 = 53 phrase assertions across all 5 matchers, plus mixed-case, negative controls, empty-input, 5-way precedence sweep, and pairwise precedence tests.

<promise>PASS</promise>
