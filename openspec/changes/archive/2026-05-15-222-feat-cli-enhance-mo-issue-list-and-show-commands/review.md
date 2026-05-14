## Review: Issue #222 CLI Enhancements

### Changed Files

| File | Change |
|------|--------|
| `packages/cli/src/api/issues.ts` | Server-side stage selection, `active` alias, attention filter |
| `packages/cli/src/cli/commands/issue.ts` | CLI options for `--attention`, `--compact`, `--stat`; diff routed through API |
| `packages/cli/tests/issue-list-filters.test.ts` | 22 API-level tests |
| `packages/cli/tests/issue-list-cli.test.ts` | 13 CLI option/rendering tests |
| `packages/cli/tests/issue-show-compact.test.ts` | 11 compact show tests |
| `packages/cli/tests/issue-diff-stat.test.ts` | 10 diff stat tests |
| `packages/cli/tests/issue-cli-enhancements-regression.test.ts` | 24 regression tests |

### Correctness

**PASS.** Core logic is correct across all five features.

- `parseStageSelection` (`issues.ts:52-77`) uses a predicate-based `StageSelector` pattern. The `active` alias predicate (`issues.ts:63`) checks `PIPELINE_STAGES.has(issue.stage) && !isTerminalStatus(issue.status)`, correctly excluding backlog and terminal-status issues. Multi-stage inputs produce independent selectors composed with `selectors.some()` (OR) at `issues.ts:587`.
- `isAttentionIssue` (`issues.ts:79-91`) covers awaiting approval, blocked/interrupted status, and four delivery classifications (`blocked`, `build-failed`, `conflict`, `done-not-merged`). Normal running/probing issues have `status=active` and none of these conditions, so they are excluded.
- Filter composition order in the route handler (`issues.ts:582-599`) applies: archive scope → stage selectors → priority → label → attention. All non-stage filters are AND semantics.
- Compact show (`issue.ts:345-352`) prints one line and returns early before fetching sessions/executions. Default show path is unchanged.
- Diff stat (`issue.ts:869-884`) prints file-level additions/deletions without patch content. Both default and stat modes call the same `/issues/:number/diff` API endpoint, guaranteeing identical base/head/merge-base semantics.

**Warning:** Default `mo issue diff` changed from two-dot `git diff main mo/issue-N` to merge-base comparison via the server API. This is an intentional improvement documented in design decision D5, but users relying on two-dot diff output may notice different results when the base branch has advanced.

### Complexity

**PASS.** All new functions are under 25 lines. `parseStageSelection` has cyclomatic complexity ~5. `isAttentionIssue` has complexity ~6. The route handler's filter section is sequential and flat.

### Test Coverage

**PASS.** 80 tests across 5 files, all passing. Coverage includes:
- API-level tests with real in-memory SQLite database (`issue-list-filters.test.ts`)
- CLI option parsing, parameter forwarding, and output rendering (`issue-list-cli.test.ts`, `issue-show-compact.test.ts`, `issue-diff-stat.test.ts`)
- Regression tests covering all acceptance criteria (`issue-cli-enhancements-regression.test.ts`)

**Warning (minor):** No explicit test for `MergeState.BuildFailed` and `MergeState.Blocked` delivery states in the attention filter. The `conflict` and `done-not-merged` paths are tested, and the underlying `classifyMergeDelivery` is well-understood, but the two untested delivery branches are a gap.

### Security

**PASS.** Stage input is validated server-side; unknown stages produce a 400 error with no risk of injection. No secrets are exposed.

### Spec Compliance

| # | Acceptance Criterion | Status | Evidence |
|---|----------------------|--------|----------|
| 1 | `mo issue list -s active` returns pipeline issues, not backlog | **PASS** | `issues.ts:63` predicate + `issue-list-filters.test.ts:107-139` |
| 2 | `mo issue list -s build,check` returns OR results | **PASS** | `issues.ts:587` selectors.some() + `issue-list-filters.test.ts:143-154` |
| 3 | Invalid stage returns clear error + non-zero exit | **PASS** | `issues.ts:73` error message + `issue-list-filters.test.ts:165-186`, `issue-list-cli.test.ts:163-181` |
| 4 | Stage filter composes with priority/label/archived/all (AND) | **PASS** | `issues.ts:586-599` sequential filters + `issue-list-filters.test.ts:189-238` |
| 5 | `--attention` returns attention items | **PASS** | `issues.ts:79-91` + `issue-list-filters.test.ts:241-331` |
| 6 | `--attention` excludes normal running/probing | **PASS** | `issue-list-filters.test.ts:288-297` |
| 7 | `--attention` composes with stage/priority/label | **PASS** | `issue-list-filters.test.ts:299-331`, `issue-list-cli.test.ts:229-249` |
| 8 | `--attention` empty state is explicit | **PASS** | `issue.ts:282-284` "No issues requiring attention" + `issue-list-cli.test.ts:143-159` |
| 9 | No `--my` flag | **PASS** | `issue-list-cli.test.ts:222-226` |
| 10 | `--compact` outputs one-line summary | **PASS** | `issue.ts:345-352` + `issue-show-compact.test.ts:37-67` |
| 11 | Default show remains full detail | **PASS** | `issue-show-compact.test.ts:170-227` |
| 12 | `diff --stat` outputs file-level stats, no patch | **PASS** | `issue.ts:869-884` + `issue-diff-stat.test.ts:82-104` |
| 13 | Default diff remains full patch | **PASS** | `issue.ts:886-899` + `issue-diff-stat.test.ts:143-160` |
| 14 | Unavailable diff states are distinct | **PASS** | `issue.ts:855-866` + `issue-diff-stat.test.ts:182-240` (all 4 reasons tested) |
| 15 | CLI help covers new options | **PASS** | `issue.ts:235,240,333,839` + `issue-cli-enhancements-regression.test.ts:339-387` |

### Typecheck

**PASS.** `npm run typecheck` completes with zero errors.

### Tests

**PASS.** `npm test` — 80/80 tests pass across all 5 new test files.

<promise>PASS</promise>
