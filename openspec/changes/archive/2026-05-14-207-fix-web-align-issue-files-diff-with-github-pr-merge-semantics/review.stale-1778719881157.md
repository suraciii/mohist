## Review: Issue #207 — Align issue files diff with GitHub PR merge semantics

### Summary

The implementation successfully changes the issue diff semantic from two-dot (`base head`) to three-dot merge-base (`base...head`) and adds comparison metadata, a commits section on Issue Detail, and PR-style merge framing on both surfaces. The backend is clean and well-structured. However, there are **3 failing frontend tests** that must be fixed before this can pass review.

---

### Correctness

**ERROR: 3 frontend tests fail** in `IssueChangedFilesPage.test.tsx`:

1. **`renders raw patch content when Raw mode is selected`** — Test clicks `foo.ts` in the tree, then clicks `Raw`, and expects a `Copy` button from `RawPatchPane`. But the page now renders all files in a continuous diff flow by default (line 395–418 of `IssueChangedFilesPage.tsx`). The `readerMode === 'raw'` branch at line 383 requires `selectedFile` to be non-null, but the continuous flow always runs regardless of `selectedFile`. When the test clicks `foo.ts` in the tree, `selectedFile` is set, but the rendering condition at line 382 still checks `selectedFile` — however the continuous flow at line 394 always renders. The test selects Raw mode and expects `RawPatchPane` for the selected file, but the continuous flow rendering block (lines 394–419) unconditionally renders `UnifiedDiffPane`/`SplitDiffPane` for ALL blocks. The `raw`/`full`/`search` modes only activate when a specific file is selected AND the continuous flow is bypassed — but the current code structure means raw mode is unreachable for any file when continuous mode is active. This is either a logic bug in the continuous rendering or a test that was not updated to match the new behavior.

2. **`enters commit mode when a commit is selected`** — `screen.getByRole('combobox')` finds multiple elements. The toolbar now has both a `<select>` for the reader mode (line 297–306) and a `<select>` for commit selection (line 333–348). The test uses `getByRole('combobox')` which matches both.

3. **`exits commit mode when Exit commit mode is clicked`** — Same root cause as #2.

**Fix for tests 2 & 3**: Use `screen.getAllByRole('combobox')` and select the correct one (the commit selector), or use a more specific query like `screen.getByText('View commit...')` to find the commit dropdown's parent.

**Fix for test 1**: The test was written for the old "select a file first" interaction model. With the continuous diff flow, raw mode needs to work differently — either the continuous flow should respect `readerMode`, or the test should verify raw mode works through a different path (e.g., clicking a per-file action in the continuous view). This is also a design gap: the spec says "advanced modes remain available as secondary controls" but the continuous flow does not integrate them.

**Minor issue**: In `IssueChangedFilesPage.tsx` line 382–386, `RawPatchPane`, `FullFilePane`, and `DiffSearchPane` only render when `selectedFile` is non-null. But with the continuous flow as the default (line 394), these modes are effectively dead code unless a user first selects a file from the tree, which contradicts the spec's "continuous diff flow by default" requirement.

---

### Complexity

- `resolveIssueComparisonContext` in `issues.ts:171-239` is well-scoped at ~70 lines with clear early returns. Good.
- `IssueChangedFilesPage.tsx` at 437 lines is reasonable but dense. The toolbar and rendering logic could benefit from extraction into sub-components, but this is not blocking.
- All functions stay under reasonable complexity thresholds.

---

### Test Coverage

**Backend tests** (10 tests, all passing):
- ahead-only branch: two-dot and three-dot match ✅
- ahead+behind branch: base-only files excluded ✅
- commits API consistency with diff API ✅
- comparison metadata presence and correctness ✅
- unavailable states (not_started, worktree_removed) ✅
- commit-scoped diff isolation ✅

**Frontend tests**:
- `IssueDetailPage.test.tsx`: 24 tests, all passing ✅
- `IssueChangedFilesPage.test.tsx`: 29 tests, **3 failing** ❌

The failing tests are due to the page being restructured to continuous flow without updating the test selectors and raw-mode assertions.

---

### Security

- Commit hash is validated with regex `/^[0-9a-f]{7,40}$/i` at `issues.ts:2270`. Good.
- No injection risks. All git commands use `execFile` with argument arrays.
- No secrets exposed.

---

### Spec Compliance

#### Diff Semantics

| Criterion | Verdict | Evidence |
|-----------|---------|----------|
| Diff API returns merge-base comparison | **PASS** | `issues.ts:1888` uses `${ctx.base}...${ctx.head}` three-dot diff |
| Response includes base, head, mergeBase, ahead, behind, canFastForward, comparison=merge-base | **PASS** | `issues.ts:1949-1958` builds `IssueDiffResponse` with all fields; test at `issue-merge-base-regression.test.ts:450-457` asserts all present |
| Behind-base branches exclude base-only files | **PASS** | `issue-merge-base-regression.test.ts:259-262` asserts `main-only.txt` not in file list |
| Commits API shares comparison metadata | **PASS** | `issues.ts:2231-2240` includes same fields; test at line 500-507 verifies cross-endpoint consistency |
| Commit diff remains commit-scoped | **PASS** | `issues.ts:2264-2398` uses `git show --patch hash` (single commit); test at line 352-410 confirms |
| Unavailable states return correct reason codes | **PASS** | `issues.ts:185-201` returns `not_started`, `worktree_removed`, `branch_missing`, `git_error`; tests at lines 514-581 verify |

#### Files Changed Page

| Criterion | Verdict | Evidence |
|-----------|---------|----------|
| Header frames `head wants to merge into base` | **PASS** | `IssueChangedFilesPage.tsx:229-232` renders merge framing text |
| Shows `showing merge-base → issue head` | **PASS** | `IssueChangedFilesPage.tsx:257` renders the label |
| Behind-base notice | **PASS** | `IssueChangedFilesPage.tsx:262-269` renders blue notice when `isBehind` |
| Continuous diff flow by default | **PASS** | `IssueChangedFilesPage.tsx:395-418` renders all `parsedBlocks` unconditionally in the main reader |
| Commit-scoped reading remains available | **PASS** | `IssueChangedFilesPage.tsx:331-357` has commit selector dropdown and exit button |
| Advanced modes as secondary controls | **PARTIAL** | Mode dropdown exists in toolbar (line 296-306), but Raw/Full/Search modes don't work with the continuous flow because they only render when `selectedFile` is non-null (lines 382-386) while the continuous flow (lines 394-419) always takes the else branch |

#### Issue Detail Commits

| Criterion | Verdict | Evidence |
|-----------|---------|----------|
| Commits section with count | **PASS** | `IssueDetailPage.tsx:382-414` renders `Commits ({count})` |
| Short hash, subject, relative time | **PASS** | `IssueDetailPage.tsx:405-408` renders `commit.shortHash`, `commit.message`, `formatRelativeTime(commit.date)` |
| View all commits button | **PASS** | `IssueDetailPage.tsx:388-393` navigates to `/issue/${issueNumber}/files` |
| Unavailable states render clear message | **PASS** | `IssueDetailPage.tsx:416-424` renders unavailable message for `not_started`, `worktree_removed`, etc. |
| Merge framing in changes summary | **PASS** | `IssueDetailPage.tsx:291-331` shows `wants to merge into`, ahead/behind, files changed, merge-base label |
| View files button | **PASS** | `IssueDetailPage.tsx:372-377` navigates to `/issue/${issueNumber}/files` |

#### Consistency

| Criterion | Verdict | Evidence |
|-----------|---------|----------|
| Both surfaces use same merge-base diff summary | **PASS** | Both consume `useIssueDiff` which calls the same `/diff` endpoint returning merge-base data; test `issue-merge-base-regression.test.ts:196` verifies diff and commits API return same `filesChanged` |
| Commit list uses base..head semantics | **PASS** | `issues.ts:2151-2153` uses `git log ${ctx.base}..${ctx.head}` |
| Issue Detail remains lightweight | **PASS** | No inline diff viewer; only summary rows and navigation |

#### Reading Context Preservation

| Criterion | Verdict | Evidence |
|-----------|---------|----------|
| Restores reading context across navigation | **PASS** | `IssueChangedFilesPage.tsx:15-45` uses sessionStorage keyed by issue number; restores selectedFile, diffMode, readerMode, scrollTop |

---

### Warnings

1. **IssueChangedFilesPage continuous flow breaks raw/full/search modes**: When `readerMode` is `raw`/`full`/`search`, the rendering at line 382 checks `selectedFile`. If no file is selected from the tree, the continuous flow at line 394 renders instead, ignoring the reader mode. This means raw/full/search modes are effectively unreachable unless a user first clicks a file in the tree. The spec says these should be "secondary reading controls" — they are present but non-functional in the continuous flow default.

2. **IssueDetailPage commits section: no navigation to commit-specific view**: The spec says "each commit item can navigate to commit-specific inspection in the changed-files reader." The commit rows display info but don't have clickable navigation to a specific commit's diff. The `View all commits` button goes to the files page but not to a specific commit. This is a minor gap.

3. **Unreachable summary field**: In `issues.ts:1960`, the diff response sets `summary.commits` to `0`. The `ChangesSummary` type includes `commits` but the diff endpoint doesn't compute it. The commits endpoint correctly sets it at line 2243. Not a bug, but the type reuse is slightly misleading.

---

### Error-Level Issues

1. **3 failing tests in `IssueChangedFilesPage.test.tsx`** — Tests were not updated after the continuous diff flow was introduced. The combobox selector finds multiple elements, and raw mode assertion fails because the continuous flow bypasses the raw pane.

---

### Fix Suggestions

1. **`IssueChangedFilesPage.test.tsx:404,413`** — Replace `screen.getByRole('combobox')` with a more specific selector, e.g.:
   ```ts
   const selects = screen.getAllByRole('combobox')
   const commitSelect = selects.find(s => (s as HTMLSelectElement).value === '')
   ```
   Or find the commit dropdown via its "View commit..." option text.

2. **`IssueChangedFilesPage.test.tsx:378-385`** — The raw mode test needs to account for the continuous flow. Either:
   - Fix the continuous flow to respect `readerMode` (render raw for each file block when mode is `raw`), or
   - Update the test to verify that clicking `Raw` changes the mode dropdown value and that selecting a file then shows the raw pane.

3. **Design gap in continuous flow** — The `readerMode` state is set but ignored by the continuous rendering block. Consider integrating mode awareness into the continuous flow, or clearly marking raw/full/search as "selected-file-only" secondary modes.

<promise>FAIL</promise>
