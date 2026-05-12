# Review: Issue #189 — Switch issue diff to two-argument base-vs-head comparison

## Correctness

### Issue diff API: two-argument comparison ✅
`packages/cli/src/api/issues.ts:1711` correctly uses `['diff', project.baseBranch, branchName]` (two separate arguments) instead of the previous `['diff', '${baseBranch}...${branchName}']` (three-dot). Both `--numstat` (line 1717) and full diff (line 1718) share this same `diffArgs`, so summary and per-file patches stay aligned — per design D2.

### CLI command: two-argument comparison ✅
`packages/cli/src/cli/commands/issue.ts:749` correctly switched from `git diff ${baseBranch}...${branchName}` to `git diff ${baseBranch} ${branchName}`.

### Shell injection in CLI — pre-existing concern ⚠️
`issue.ts:749` interpolates `baseBranch` and `branchName` directly into a template string for `execSync`. `branchName` is derived from `mo/issue-${number}` where `number` is `parseInt`-ed (safe), and `baseBranch` comes from user-configured `project.baseBranch` (could contain shell metacharacters). This is a pre-existing vulnerability not introduced by this change.

### Commits endpoint retains three-dot diff — intentional, but inconsistent ⚠️
`packages/cli/src/api/issues.ts:1959` still uses `git diff ${project.baseBranch}...${branchName} --numstat` for the commits summary. This is intentional per design D3, but creates a subtle inconsistency: the `summary.filesChanged` field in `GET /api/issues/:number/commits` will report a different (larger) file count than `GET /api/issues/:number/diff` for merge-forward branches. Not a bug per spec, but worth tracking.

## Complexity

All changed functions remain under 50 lines. Cyclomatic complexity is low — the changes are single-line substitutions in two call sites plus a two-argument array instead of a string-interpolated single argument. Test functions are longer but are integration tests exercising git workflows, which is appropriate.

## Test Coverage

Four new integration tests cover the merge-forward regression:

| Test | What it verifies |
|------|------------------|
| `excludes base-branch changes from issue diff when issue branch has merged base forward` | Core scenario: after merge-forward, base-only files and already-merged issue files are excluded |
| `keeps diff summary and per-file patch consistent after merge-forward range change` | `summary.filesChanged` matches `files.length`, patch content present |
| `does not broaden commit diff behavior after two-dot fix` | Commits endpoint still returns valid data after the fix |
| `issue diff uses two-argument base-vs-head comparison not three-dot merge-base` | After merging base into branch, only post-merge issue files appear |

All 20 tests pass. Typecheck passes.

**Gap:** No test verifies the `available: true/false` + `reason` fields on merge-forward branches (the existing tests at lines 300–373 cover availability for non-merge scenarios). This is acceptable since the availability logic is unchanged.

**Gap:** No test verifies the `--numstat` counts (additions/deletions) are correct in the merge-forward scenario. Test 2 checks `files.length === summary.filesChanged` but not that `summary.additions`/`summary.deletions` match the actual file-level counts. Low risk — the same `diffArgs` drives both calls.

## Security

No new secrets or credentials introduced. The pre-existing `execSync` shell interpolation concern is noted above but is not part of this change.

## Spec Compliance

### http-api/spec.md

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Diff API available: returns `available: true`, `reason: null`, `base`, `head`, `summary`, `files` with per-file diff | ✅ PASS | Line 1711 uses two-argument diff; existing test at line 333 verifies full available response; new tests verify merge-forward correctness |
| Diff excludes merged base-branch changes | ✅ PASS | Test `excludes base-branch changes` at line 637 asserts `filePaths` does NOT contain `base.txt` or `issue-only-2.txt` |
| Review data unavailable: `available: false` with `reason` | ✅ PASS | Unchanged from existing implementation; existing tests cover all four reasons |

### issue-review-surface/spec.md

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Files changed is default/primary view, shows changed file list with adds/dels | ✅ PASS (behavioral) | No UI changes needed; backend now returns cleaner file set; existing ChangesPanel renders the same data |
| Merged base changes do not pollute file review | ✅ PASS | Test `excludes base-branch changes` verifies base-merged files are excluded |
| No file changes shows "No file changes yet" | ✅ PASS (behavioral) | Existing ChangesPanel test at line 102 verifies empty-file-set rendering; two-argument diff correctly returns empty set when branches are identical |

## Summary

The implementation is minimal, correct, and well-aligned with the design. Two call sites changed (API + CLI), four integration tests added covering the merge-forward regression. No unnecessary scope creep. One advisory note about the commits endpoint's three-dot `summary.filesChanged` being inconsistent with the diff endpoint, but that is explicitly out of scope per design D3.

<promise>PASS</promise>