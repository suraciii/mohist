# Review Report

## Result: PASS

The original multi-commit parser issue is fixed, the review APIs consistently expose lifecycle availability states, and regression coverage now exercises the previously missed missing-worktree paths. Overall result is PASS.

## Dimensions

### Correctness: PASS

Fixed: Commit diff API returns an availability-aware lifecycle payload when a worktree is missing.

- File: `packages/cli/src/api/issues.ts:1786-1791`
- Evidence: after `worktreeManager.exists(project.name, issue.number)` returns false, `GET /api/issues/:number/commits/:hash/diff` now returns `success: true` with `available: false`, `reason: 'not_started'` for Draft/Backlog or `reason: 'worktree_removed'` for later lifecycle stages.

Fixed: Commits API classifies Draft/Backlog issues with no worktree as not started when a `WorktreeManager` exists.

- File: `packages/cli/src/api/issues.ts:1557-1577`
- Evidence: for Draft/Backlog issues where `worktreeManager` is present but `exists(...)` is false, `GET /api/issues/:number/commits` now returns `available: false`, `reason: 'not_started'`, matching the diff endpoint's lifecycle semantics.

Fixed: Commits API now preserves multi-commit boundaries.

- Evidence: `git log` emits a real record separator with `--format=%x1e%H%x00%h%x00%s%x00%an%x00%aI` at `packages/cli/src/api/issues.ts:1621-1624`, parsing splits on `\x1e` at `packages/cli/src/api/issues.ts:1639-1646`, and numstat lines are associated with each parsed commit at `packages/cli/src/api/issues.ts:1656-1674`.

Fixed: Commit summary file count no longer over-counts repeated per-commit file edits.

- Evidence: commits response summary now computes `filesChanged` from a base-to-head `git diff ... --numstat` file set at `packages/cli/src/api/issues.ts:1626` and `packages/cli/src/api/issues.ts:1689-1708`, rather than summing each commit's `filesChanged`.

### Complexity: PASS

- Evidence: the parser fix in `packages/cli/src/api/issues.ts:1617-1713` stays procedural and localized to the commits route. The test wiring change in `packages/cli/package.json:19` is simple.
- Risk: availability handling is now duplicated and subtly inconsistent across diff, commits, and commit-diff handlers, which caused the correctness failures above. This should be fixed by aligning the existing branches rather than adding a larger abstraction unless more review endpoints are added.

### Test Coverage: PASS

Fixed: backend regression tests now catch the original multi-commit parser failure.

- Evidence: `packages/cli/tests/review-surface.test.ts:142-147` asserts exactly three commits and verifies messages, touched files, additions, and deletions. `packages/cli/tests/review-surface.test.ts:189-199` separately verifies per-commit numstat ownership.

Fixed: web component tests cover the Changes panel behavior.

- Evidence: `packages/cli/web/tests/ChangesPanel.test.tsx` verifies files-first behavior, workspace removed copy, summary header rendering, commit list rows, and commit expansion.

Fixed: tests cover missing-worktree behavior when a real `WorktreeManager` is present.

- File: `packages/cli/tests/review-surface.test.ts:376-545`
- Evidence: `packages/cli/tests/review-surface.test.ts` now includes tests for a real `WorktreeManager` with no issue worktree, covering commits `not_started` and commit-diff `worktree_removed` responses.

### Security: PASS

- Evidence: commit hash input is still validated with `/^[0-9a-f]{7,40}$/i` at `packages/cli/src/api/issues.ts:1735-1740`. Git commands are executed with `execFileAsync` argument arrays at `packages/cli/src/api/issues.ts:1331`, `packages/cli/src/api/issues.ts:1366-1368`, `packages/cli/src/api/issues.ts:1621-1626`, `packages/cli/src/api/issues.ts:1812-1815`, and `packages/cli/src/api/issues.ts:1835-1838`, avoiding shell interpolation.
- Residual risk: branch names are derived from numeric issue numbers and base branch comes from project configuration; no new secret exposure was found.

### Spec Compliance: PASS

1. PASS: Retained worktree summary shows base/head, files, commits, additions/deletions, worktree state, and merge truth in `packages/cli/web/src/components/IssueDetailPage.tsx:224-251`.
2. PASS: Files changed defaults as the primary view via `useState<'files' | 'commits'>('files')` in `packages/cli/web/src/components/IssueDetailPage.tsx:41`, and file rows plus expandable file diffs render in `packages/cli/web/src/components/ChangesPanel.tsx:172-214`.
3. PASS: Commits API now returns complete multi-commit ranges with preserved boundaries through `--format=%x1e...` and `rawOutput.split('\x1e')` in `packages/cli/src/api/issues.ts:1621-1646`.
4. PASS: Commit rows include hash, message, time, touched files, additions, and deletions in `packages/cli/web/src/components/ChangesPanel.tsx:36-54`.
5. PASS: Commit expansion lazily calls `useCommitDiff(issueNumber, commit.hash, expanded)` in `packages/cli/web/src/components/ChangesPanel.tsx:17` and renders patch output through `DiffViewer` in `packages/cli/web/src/components/ChangesPanel.tsx:67`.
6. PASS: mohist#146-style multi-commit issues should no longer collapse to one returned commit; targeted regression tests verify three commits at `packages/cli/tests/review-surface.test.ts:142-147`.
7. PASS: Worktree removed state is consistently availability-aware across review APIs; commit-diff returns `available: false`, `reason: 'worktree_removed'` when `worktreeManager.exists(...)` is false for later lifecycle stages.
8. PASS: Backlog/not-started issues return `available: false`, `reason: 'not_started'` for diff, commits, and commit-diff availability checks.
9. PASS: Done plus retained worktree remains reviewable because handlers proceed to branch and git range checks when `worktreeManager.exists(...)` is true in `packages/cli/src/api/issues.ts:1314-1327`, `packages/cli/src/api/issues.ts:1557-1583`, and `packages/cli/src/api/issues.ts:1786-1792`.
10. PASS: Archived/cleaned issues are not required to retain diff/commits; missing worktrees can end the review lifecycle, but the commit-diff endpoint still needs to express this as an availability payload rather than 404.
11. PASS: No new external diff rendering dependency was introduced; file and commit patches reuse `DiffViewer` in `packages/cli/web/src/components/ChangesPanel.tsx:3`, `packages/cli/web/src/components/ChangesPanel.tsx:67`, and `packages/cli/web/src/components/ChangesPanel.tsx:212`.

## Fix Suggestions

No blocking fixes remain. Keep an eye on future duplication across review API availability branches if more review endpoints are added.

## Verification

- `npm test -- review-surface.test.ts`: PASS.
- `npm --prefix web run test:run -- ChangesPanel.test.tsx`: PASS.
- `npm run build`: PASS. Build completed; npm reported existing dependency audit issues: 1 moderate and 1 high vulnerability.

<promise>PASS</promise>
