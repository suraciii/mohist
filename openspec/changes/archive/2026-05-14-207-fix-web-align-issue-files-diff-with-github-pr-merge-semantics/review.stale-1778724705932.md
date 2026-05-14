## Review: fix(web): align issue files diff with GitHub PR merge semantics

**Reviewer**: automated code review
**Date**: 2026-05-14
**Files changed**: 17 files, +1760 -200 lines

---

### Summary

The implementation introduces a shared `resolveIssueComparisonContext` helper in the backend that computes merge-base metadata once and feeds both the diff and commits endpoints, switching the diff semantic from two-dot (`git diff base head`) to three-dot (`git diff base...head`). The frontend consumes the new `ComparisonMetadata` shape to render merge-framed headers, a behind-base notice on the files page, a lightweight commits section on Issue Detail, and continuous diff rendering by default.

---

### Correctness

**PASS** — The core semantic change is correct:

- `GET /api/issues/:number/diff` uses `git diff ${ctx.base}...${ctx.head}` (`issues.ts:1888`), which is equivalent to `git diff <merge-base> <head>`. This excludes base-only changes when the branch is behind.
- `GET /api/issues/:number/commits` uses `git log ${ctx.base}..${ctx.head}` (`issues.ts:2153`) for commit listing and `git diff ${ctx.base}...${ctx.head} --numstat` (`issues.ts:2156`) for the summary `filesChanged`, keeping both endpoints consistent.
- `GET /api/issues/:number/commits/:hash/diff` remains a single-commit `git show` (`issues.ts:2367-2370`) and does not redefine issue-level semantics.
- Both diff and commits endpoints share the same `resolveIssueComparisonContext` helper (`issues.ts:171-239`), which verifies worktree existence, branch existence, resolves merge-base via `git merge-base`, and derives `ahead`/`behind`/`canFastForward` from `WorktreeManager.getWorktreeStatus`.
- Unavailable states (`not_started`, `worktree_removed`, `branch_missing`, `git_error`) are handled consistently.

**Minor concern**: Diff response sets `summary.commits = 0` (`issues.ts:1961`) because the diff endpoint does not query commit data. While not harmful, it could mislead API consumers.

---

### Complexity

**PASS** — `resolveIssueComparisonContext` is a well-scoped 68-line helper. The diff and commits handlers are each under 100 lines. Frontend components stay under 440 lines. No function exceeds 50 lines or cyclomatic complexity 10.

---

### Test Coverage

**PASS** — All tests pass:

- Backend: `issue-merge-base-regression.test.ts` — 10 tests covering ahead-only, ahead+behind, commit-scope isolation, metadata correctness, and unavailable states.
- Frontend: `IssueDetailPage.test.tsx` — 16 tests covering merge framing, commits section, unavailable states, navigation.
- Frontend: `IssueChangedFilesPage.test.tsx` — 37 tests covering rendering, unavailable states, file tree, large diffs, mode switching, commit-scoped reading.

---

### Security

**PASS** — Commit hash is validated with regex (`/^[0-9a-f]{7,40}$/i`, `issues.ts:2270`) before use. No injection risks. No secrets exposed.

---

### Spec Compliance

#### http-api/spec.md

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Diff returns merge-base comparison | **PASS** | `issues.ts:1888` uses `git diff base...head` |
| Behind-base excludes base-only files | **PASS** | Three-dot diff semantics; tests confirm |
| Commits shares comparison metadata | **PASS** | Both use `resolveIssueComparisonContext` |
| Commit diff remains commit-scoped | **PASS** | `issues.ts:2367` uses `git show` |
| Unavailable lifecycle states | **PASS** | Tests confirm `not_started`, `worktree_removed`, `branch_missing` |

#### issue-changed-files-reader/spec.md

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Merge framing in header | **PASS** | `IssueChangedFilesPage.tsx:229-258` |
| Behind-base notice | **PASS** | `IssueChangedFilesPage.tsx:262-269` |
| Continuous diff flow | **PASS** | `IssueChangedFilesPage.tsx:394-419` renders all blocks by default |
| Filter files by path | **PASS** | Tree filter input exists |
| Advanced modes secondary | **PASS** | Mode selector is a dropdown; commit selector is secondary |
| Commit-scoped reading available | **PASS** | `IssueChangedFilesPage.tsx:331-349` |

#### issue-review-surface/spec.md

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Consistent counts across surfaces | **PASS** | Both use same merge-base diff source |
| Behind-base excludes base-only | **PASS** | Three-dot diff |
| Commit list on detail page | **PASS** | `IssueDetailPage.tsx:382-414` |
| **Commit items navigable** | **FAIL** | See Error #1 below |
| Commit section unavailable state | **PASS** | `IssueDetailPage.tsx:416-424` |

#### web-ui/spec.md

| Criterion | Status | Evidence |
|-----------|--------|----------|
| View files from Issue Detail | **PASS** | `IssueDetailPage.tsx:372-379` |
| Commit summary on detail | **PASS** | Shows count, short hash, message, time |
| Issue Detail lightweight | **PASS** | No inline diff viewer |
| Reading context preservation | **PASS** | `sessionStorage` in `IssueChangedFilesPage.tsx:25-45` |

---

### Errors

#### Error #1: Commit rows on IssueDetailPage are not navigable

**Spec**: `issue-review-surface/spec.md` Scenario "Commit navigation":
> WHEN a user activates a commit item from Issue Detail, THEN the user can navigate to commit-specific inspection in the changed-files reader

**Spec**: `web-ui/spec.md` Scenario "Issue Detail commit summary":
> each commit item can navigate to commit-specific inspection in the changed-files reader

**Acceptance criterion** (T-002):
> Commit rows provide a navigation path into the files page commit view or equivalent commit-specific inspection surface

**Current state**: `IssueDetailPage.tsx:399-412` renders commit rows as plain `<div>` elements with no `onClick` handler, no `cursor-pointer` class, and no keyboard accessibility. They are display-only.

**Suggested fix**: `IssueDetailPage.tsx:399-412` — make each commit row a clickable element that navigates to the files page in commit mode:

```tsx
<button
  key={commit.hash}
  onClick={() => navigate(`/issue/${issueNumber}/files?commit=${commit.hash}`)}
  className="flex items-center justify-between text-sm group w-full text-left hover:bg-gray-50 rounded px-1 py-0.5 transition-colors"
>
  ...
</button>
```

This requires the files page to read `?commit=<sha>` from query params and auto-enter commit mode on load.

---

### Warnings

#### Warning #1: Redundant merge summary cards on IssueDetailPage

`IssueDetailPage.tsx:291-330` and `IssueDetailPage.tsx:360-380` both show head → base, files changed, additions, deletions. The second card adds only the "View files" button. Consider consolidating into a single card with the "View files" action to reduce visual clutter.

#### Warning #2: Diff response `summary.commits = 0`

`issues.ts:1961` sets `commits: 0` in the diff summary. While the `ChangesSummary` type requires the field, returning 0 when no commit count was computed could mislead consumers. Consider omitting the field or computing it from the comparison context.

#### Warning #3: Commits API additions/deletions may diverge from diff API

`issues.ts:2228-2229` sums per-commit `additions`/`deletions` from `git log --numstat`, while the diff endpoint (`issues.ts:1935-1947`) sums from `git diff --numstat`. When commits have overlapping changes (modify same lines), per-commit sums exceed the aggregate diff. The spec says "summary counts are consistent," which is met for `filesChanged` (both use `git diff base...head --numstat`), but additions/deletions can differ.

---

### Build & Test Verification

- TypeScript: **PASS** (zero errors)
- Backend tests: **10/10 PASS**
- Frontend tests: **53/53 PASS**

<promise>FAIL</promise>
