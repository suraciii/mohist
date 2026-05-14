## Review

Reviewed implementation across 4 tasks: T-001 (backend API), T-002 (Issue Detail), T-003 (Files changed page), T-004 (test coverage). All typecheck and build pass. 63 tests pass (10 backend, 24 IssueDetailPage, 29 IssueChangedFilesPage).

### Correctness

**Backend** (`packages/cli/src/api/issues.ts`):
- `resolveIssueComparisonContext` (lines 171-239) is a clean shared helper that checks project/worktree/branch/base availability, resolves merge-base SHA, and derives ahead/behind/canFastForward from `WorktreeManager.getWorktreeStatus`. Well-designed per D1.
- Diff endpoint (line 1888) correctly uses `git diff ${ctx.base}...${ctx.head}` (three-dot) for merge-base semantics.
- Commits endpoint (line 2153) correctly uses `git log ${ctx.base}..${ctx.head}` for reachable commits and `git diff ${ctx.base}...${ctx.head} --numstat` for summary filesChanged — ensuring consistency with diff endpoint.
- Commit-diff endpoint (lines 2264-2399) remains commit-scoped via `git show --format= --patch hash`. Validates hash regex and branch membership.

**Frontend types** (`packages/cli/web/src/lib/types.ts`):
- `ComparisonMetadata`, `ChangesAvailability`, `IssueDiffResponse`, `IssueCommitsResponse`, `CommitDiffResponse` correctly mirror backend shapes.
- `CommitDiffResponse` intentionally excludes `ComparisonMetadata` — correct per D2.

**IssueDetailPage** (`packages/cli/web/src/components/IssueDetailPage.tsx`):
- Merge summary (lines 291-331) shows `{head} wants to merge into {base}`, ahead/behind, files changed, additions/deletions, and `showing merge-base → {head}`.
- Commits section (lines 382-414) shows up to 5 commits with shortHash, message, relative time.
- Unavailable states (lines 416-424) display reason messages.

**IssueChangedFilesPage** (`packages/cli/web/src/components/IssueChangedFilesPage.tsx`):
- Header (lines 227-259) shows merge framing with all metadata.
- Behind-base notice (lines 262-269) is a non-blocking blue banner.
- Continuous diff flow (lines 394-419) renders all file blocks in sequence by default.
- Reading position restoration (lines 87-127) uses sessionStorage to persist state per issue.

### Complexity

- `resolveIssueComparisonContext` (68 lines): linear flow, acceptable.
- Diff/commits handlers (~120-145 lines): some length but necessarily detailed for git output parsing.
- No function exceeds 50 lines besides the route handlers, which are acceptable for API endpoint code.
- No cyclomatic complexity concerns.

### Security

- Commit hash validated via `/^[0-9a-f]{7,40}$/i` regex (line 2270).
- All git commands use `execFileAsync` (not shell interpolation).
- No exposed secrets or injection risks.

### Test Coverage

- **Backend**: 10 tests covering ahead-only, ahead+behind, commit scope, metadata fields, metadata consistency, unavailable states (not_started, worktree_removed).
- **IssueDetailPage**: 24 tests covering merge framing, commits section, unavailable states, View files navigation, behind-base copy.
- **IssueChangedFilesPage**: 29 tests covering route rendering, unavailable states, reader controls, file tree, large diff, raw mode, search, commit mode, reading position.
- All 63 tests pass.

### Spec Compliance

#### Diff semantics — all PASS

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Diff API returns merge-base comparison | PASS | `issues.ts:1888` uses `git diff ${ctx.base}...${ctx.head}` |
| Behind-base excludes base-only changes | PASS | `issues-merge-base-regression.test.ts:207-266` |
| Commits API shares comparison metadata | PASS | `issues.ts:2232-2240` returns same metadata as diff |
| Commit diff remains commit-scoped | PASS | `issues.ts:2367-2369` uses `git show` |
| Review data unavailable by lifecycle | PASS | `issues.ts:1880-1886`, tests lines 514-581 |

#### Files changed page — all PASS

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Header shows merge framing | PASS | `IssueChangedFilesPage.tsx:229-233` |
| Shows merge-base → issue head | PASS | `IssueChangedFilesPage.tsx:257` |
| Behind-base non-blocking notice | PASS | `IssueChangedFilesPage.tsx:262-269` |
| Continuous diff flow by default | PASS | `IssueChangedFilesPage.tsx:394-419` renders all blocks |
| Commit inspection as secondary | PASS | Commit dropdown + Exit button |

#### Issue Detail — PASS with warnings

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Shows merge framing | PASS | `IssueDetailPage.tsx:294-297` |
| Shows files changed / View files | PASS | `IssueDetailPage.tsx:360-380` |
| Commits section with count + metadata | PASS | `IssueDetailPage.tsx:382-414` |
| Commit items navigate to inspection | **WARN** | Only "View all commits" button navigates; individual rows are not clickable |
| Unavailable states | PASS | `IssueDetailPage.tsx:416-424` |

#### Reading context preservation — PASS

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Saves/restores reading position | PASS | `IssueChangedFilesPage.tsx:87-127` uses sessionStorage |

### Warnings

1. **W1: Individual commit rows not clickable in Issue Detail** (`IssueDetailPage.tsx:399-409`): The spec requires "each commit item can navigate to commit-specific inspection in the changed-files reader" but commit rows are plain `<div>` elements with no click handler. Only the "View all commits" button provides navigation. To fix, wrap each commit row in a button or add onClick that navigates to `/issue/${issueNumber}/files?commit=${commit.hash}`.

2. **W2: Commits summary additions/deletions may differ from diff summary** (`issues.ts:2228-2229`): The commits endpoint sums per-commit additions/deletions which can double-count overlapping file changes, while the diff endpoint uses merge-base numstat. `summary.filesChanged` is correctly consistent (both use merge-base numstat), but `summary.additions`/`summary.deletions` may differ between the two responses.

3. **W3: `summary.commits` hardcoded to 0 in diff response** (`issues.ts:1962`): The `ChangesSummary` type includes `commits` but the diff endpoint always returns 0. Minor cosmetic issue — consumers should use the commits endpoint for commit counts.

4. **W4: Changes section arrow direction** (`IssueDetailPage.tsx:365-368`): The "Changes" card shows `{head} → {base}` which could be confused with the merge direction. The top summary correctly says "wants to merge into", but the arrow in the changes card reads as "head goes to base" rather than reinforcing merge intent.

<promise>PASS</promise>
