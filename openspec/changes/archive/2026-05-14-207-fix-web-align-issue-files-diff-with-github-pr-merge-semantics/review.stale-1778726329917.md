## Review

Reviewed all implementation files against proposal, design, specs, and acceptance criteria.

### Correctness

**Backend (`packages/cli/src/api/issues.ts`)**

The `resolveIssueComparisonContext` helper (lines 171-239) correctly centralizes merge-base resolution and branch validation. It reuses `WorktreeManager.getWorktreeStatus` for ahead/behind/canFastForward, avoiding a divergent second source.

- `GET /:number/diff` uses `git diff ${ctx.base}...${ctx.head}` (three-dot) — correct merge-base semantics (line 1888).
- `GET /:number/commits` uses `git log ${ctx.base}..${ctx.head}` for commit listing (line 2153) and `git diff ${ctx.base}...${ctx.head} --numstat` for summary file count (line 2156). Both are correct per design D2.
- `GET /:number/commits/:hash/diff` remains commit-scoped with `git show --patch hash` (line 2368) and returns `CommitDiffResponse` without comparison metadata — correct per spec.
- Unavailable states are handled correctly: `not_started`, `worktree_removed`, `branch_missing`, `git_error` with user-facing messages.

No logic errors, off-by-one errors, or edge case gaps found.

**Frontend**

- `IssueDetailPage.tsx`: Correctly consumes `useIssueDiff` and `useIssueCommits` for merge framing and commits section.
- `IssueChangedFilesPage.tsx`: Correctly renders continuous diff flow, merge-base header, behind-base notice, and reading state preservation via sessionStorage.
- `types.ts`: Frontend types (`ComparisonMetadata`, `IssueDiffResponse`, `IssueCommitsResponse`, `CommitDiffResponse`) match backend response shapes.

### Complexity

- `resolveIssueComparisonContext` is 68 lines (lines 171-239) — slightly over the 50-line guideline but acceptable because it handles four early-return branches for different unavailable states plus the happy path. Cyclomatic complexity is moderate.
- All other functions are within reasonable size limits.

### Test Coverage

- **Backend**: 10 tests in `issue-merge-base-regression.test.ts` covering ahead-only, ahead+behind, commit-scope isolation, metadata presence, consistency between diff/commits, and three unavailable states. All pass.
- **Frontend**: 53 tests across `IssueDetailPage.test.tsx` (20 tests) and `IssueChangedFilesPage.test.tsx` (33 tests). All pass. Tests cover merge framing, commits section, unavailable states, navigation, reader controls, file tree, large diffs, commit mode, and raw/search modes.
- **Typecheck**: Backend `tsc --noEmit` passes with no errors.

### Security

- Commit hash parameter is validated with `/^[0-9a-f]{7,40}$/i` regex (line 2270) — prevents injection.
- No secrets or credentials exposed.
- User inputs are properly handled.

### Spec Compliance

#### Diff semantics — PASS

| Criterion | Status | Evidence |
|-----------|--------|----------|
| `/api/issues/:number/diff` returns merge-base diff | PASS | `issues.ts:1888` uses `git diff ${ctx.base}...${ctx.head}` |
| Response includes base, head, mergeBase, ahead, behind, canFastForward, comparison | PASS | `issues.ts:1949-1958` |
| comparison is `merge-base` | PASS | `issues.ts:1958` hardcodes `'merge-base'` |
| Behind-base excludes base-only changes | PASS | Backend test line 259-261 verifies `main-only.txt` excluded |
| Issue detail and files page use same diff summary | PASS | Both use `useIssueDiff` → same API endpoint |
| Commit diff remains commit-scoped | PASS | `CommitDiffResponse` has no comparison metadata; `issues.ts:2368` uses `git show` |

#### Files changed page — PASS

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Header shows "wants to merge into" | PASS | `IssueChangedFilesPage.tsx:230-232` |
| Shows "showing merge-base → head" | PASS | `IssueChangedFilesPage.tsx:257` |
| Behind-base non-blocking notice | PASS | `IssueChangedFilesPage.tsx:262-268` |
| Toolbar has reading controls | PASS | `IssueChangedFilesPage.tsx:274-357` — expand/collapse, split toggle, mode select, commit selector |
| Continuous diff flow by default | PASS | `IssueChangedFilesPage.tsx:394-419` renders all blocks in sequence |
| Advanced modes are secondary | PASS | Raw/Full/Search in `<select>` dropdown, not top-level buttons |
| Commit-scoped reading available | PASS | `IssueChangedFilesPage.tsx:331-349` with dropdown selector |
| Reading position preserved across navigation | PASS | `IssueChangedFilesPage.tsx:16-44` sessionStorage save/restore |

#### Issue detail commits — **FAIL** (one criterion)

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Shows commits section with count | PASS | `IssueDetailPage.tsx:382-413`, test verifies `Commits (2)` |
| Shows short hash, subject, relative time | PASS | `IssueDetailPage.tsx:405-408` |
| Commit rows navigate to commit-specific inspection | **FAIL** | Commit rows are plain `<div>` with no click handler — `IssueDetailPage.tsx:399-410`. Grepping for `onClick.*commit` and `navigate.*commit` returns no matches. |
| Commits section unavailable states | PASS | `IssueDetailPage.tsx:416-424` shows message for unavailable diff/commits |
| Issue Detail remains lightweight | PASS | No inline diff rendering; summary cards and navigation only |

#### Issue review surface consistency — PASS (with warning)

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Consistent counts across surfaces | PASS | Both surfaces query the same diff API endpoint |
| Behind-base review shows only issue contribution | PASS | Backend three-dot diff excludes base-only files |

#### Unavailable states — PASS

| Criterion | Status | Evidence |
|-----------|--------|----------|
| not_started | PASS | `issues.ts:186`, frontend test line 273-277 |
| worktree_removed | PASS | `issues.ts:188`, frontend test line 279-283 |
| branch_missing | PASS | `issues.ts:200,212`, frontend test line 285-289 |
| git_error | PASS | `issues.ts:219,224` |

### Errors

**E1: Commit rows are not navigable (spec compliance failure)**

- **File**: `packages/cli/web/src/components/IssueDetailPage.tsx:399-410`
- **Spec**: T-002 acceptance criterion: "Commit rows provide a navigation path into the files page commit view or equivalent commit-specific inspection surface." Also: issue-review-surface spec "the user can navigate to commit-specific inspection in the changed-files reader" and web-ui spec "each commit item can navigate to commit-specific inspection in the changed-files reader."
- **Problem**: The commit list items are rendered as inert `<div>` elements with no click handler, no link, and no navigation. Only the separate "View all commits" button navigates to the files page.
- **Fix**: Make each commit row clickable. Navigate to `/issue/${issueNumber}/files?commit=${commit.hash}` (adding query-param support in the files page to auto-enter commit mode), or at minimum navigate to `/issue/${issueNumber}/files` as a fallback.

### Warnings

**W1: Commits summary additions/deletions may differ from diff summary**

- **File**: `packages/cli/src/api/issues.ts:2228-2229`
- The commits endpoint computes `summary.additions`/`summary.deletions` by summing per-commit numstat values, while the diff endpoint computes them from the merge-base diff numstat. When commits modify the same lines, these totals can differ. The spec says "summary counts are consistent with the issue-level diff response." The critical count (`filesChanged`) is consistent because both use merge-base diff, but add/del may drift.
- **Fix**: Compute additions/deletions from `summaryNumstatOutput` (which already runs `git diff ${ctx.base}...${ctx.head} --numstat`) instead of summing per-commit values.

**W2: File filter lives in tree pane, not primary toolbar**

- The spec says "primary controls focus on all-commits scope, file filtering, and diff settings" but the file filter input is inside `ChangedFilesTree` rather than the top toolbar. This is acceptable since the tree is the natural location for file filtering in a PR-style layout, but deviates from the literal toolbar wording.

<promise>FAIL</promise>
