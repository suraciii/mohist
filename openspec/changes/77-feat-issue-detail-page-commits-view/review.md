# Review Report

## Verdict: FAIL

## Dimensions

### Correctness: PASS (with warnings)

- `packages/cli/src/api/issues.ts:1228-1329` — GET /:number/commits handler works correctly in practice. The stat summary parsing on lines 1303-1311 correctly extracts `filesChanged`, `additions`, `deletions` from the git `--stat` summary line.
- **WARNING: Dead code with incorrect logic** (`issues.ts:1293-1301`): The first stat match block counts literal `+` and `-` characters in the full stat output (including diff bars like `+++++`), producing wrong values. This is harmless because the second block (`issues.ts:1303-1311`) always overrides these values for `--stat` output. The first block should be removed entirely.
- **WARNING: `%b` in format string** (`issues.ts:1270`): The format `%h%x00%s%x00%an%x00%aI%x00%b%x01` includes `%b` (commit body), which is unnecessary since only `%s` (subject/first line) is needed per spec. Body content with newlines can pollute `statLines`, though it doesn't break the summary parser in practice.
- `packages/cli/src/api/issues.ts:1331-1404` — GET /:number/commits/:hash/diff handler is correct. Branch containment check via `git branch --contains` is sound.
- Frontend types, API client, hooks, and UI components are correct and follow existing patterns.

### Complexity: PASS (with warnings)

- **WARNING: Handler too long** — The GET /:number/commits handler is ~100 lines (`issues.ts:1228-1329`). The review threshold is 50 lines. Consider extracting the commit parsing logic into a helper function.
- **WARNING: Duplicated validation boilerplate** — The project/issue/worktree validation pattern is repeated across the new endpoints and existing handlers. This is consistent with the codebase but adds ~40 lines of boilerplate per endpoint.
- Frontend components are well-decomposed: `CommitDiffView` (~25 lines, `IssueDetailPage.tsx:29-55`), `CommitRow` (~45 lines, `IssueDetailPage.tsx:57-106`), tab layout inline. Acceptable.

### Test Coverage: FAIL

- **No tests exist for the new API endpoints** (`GET /:number/commits`, `GET /:number/commits/:hash/diff`). Zero test coverage for:
  - Happy path (commits returned with correct fields)
  - Empty worktree / no commits
  - 400 (no active project)
  - 404 (issue not found, commit not in branch, no worktree)
  - Stat parsing correctness
- **No tests for frontend components** (CommitDiffView, CommitRow, tab switching). This is a lower priority gap since the frontend has limited test infrastructure.
- Pre-existing test failures (49 across 8 files) are unrelated to this change — they cover merge-queue, pipeline-controller, priority, recover-issues, etc.

### Security: PASS (with warnings)

- **No command injection risk**: `execFileAsync` uses argument arrays (not shell), so the `:hash` parameter is treated as a literal string by git.
- **WARNING: No input validation on `:hash`** (`issues.ts:1334`): The hash parameter accepts any string without validating it looks like a hex git hash. While not exploitable via command injection, a regex check like `/^[0-9a-f]{7,40}$/` would be defense-in-depth and produce cleaner error messages.
- No exposed secrets or credentials.

### Spec Compliance: PASS

#### T-001: GET /:number/commits API endpoint

| # | Criterion | Status | Evidence |
|---|-----------|--------|----------|
| 1 | Returns commits array with hash, message, author, date, filesChanged, additions, deletions | PASS | `issues.ts:1274` declares array type with all 7 fields; `issues.ts:1284-1313` populates each field; `issues.ts:1317-1321` returns `{ commits }` |
| 2 | Returns { commits: [] } when no worktree or no commits | PASS | `issues.ts:1259-1264` returns `{ commits: [] }` when no worktree; `issues.ts:1275-1276` returns empty array when `rawOutput` is empty (no commits) |
| 3 | Returns 404 when issue not found | PASS | `issues.ts:1241-1248` checks `issueService.getByNumber` and returns 404 with `Issue #${number} not found` |
| 4 | Returns 400 when no active project | PASS | `issues.ts:1233-1238` checks `getCurrentProjectId()` and returns 400 with "No active project" |
| 5 | Commits ordered newest-first | PASS | `issues.ts:1270` uses `git log baseBranch..branchName` which defaults to reverse chronological order |
| 6 | Typecheck passes | PASS | `npm run build` succeeded with zero type errors |
| 7 | Build passes | PASS | `npm run build` exited 0 |

#### T-002: GET /:number/commits/:hash/diff API endpoint

| # | Criterion | Status | Evidence |
|---|-----------|--------|----------|
| 1 | Returns { hash, diff } when hash belongs to issue branch | PASS | `issues.ts:1392-1396` returns `{ hash, diff: diffOutput.stdout }` after branch check passes |
| 2 | Returns 404 when commit does not belong to issue branch | PASS | `issues.ts:1378-1384` checks `containsOutput.stdout.trim()` and returns 404 when empty |
| 3 | Returns 404 when no worktree exists | PASS | `issues.ts:1363-1369` checks `worktreeManager.exists()` and returns 404 with "No worktree for issue" |
| 4 | Returns 404 when issue not found | PASS | `issues.ts:1345-1352` checks `issueService.getByNumber` and returns 404 |
| 5 | Returns 400 when no active project | PASS | `issues.ts:1337-1342` checks `getCurrentProjectId()` and returns 400 with "No active project" |
| 6 | Typecheck passes | PASS | `npm run build` succeeded with zero type errors |
| 7 | Build passes | PASS | `npm run build` exited 0 |

#### T-003: Frontend types, API client, React Query hooks

| # | Criterion | Status | Evidence |
|---|-----------|--------|----------|
| 1 | CommitEntry type has all 7 fields | PASS | `types.ts:93-101` defines `CommitEntry` with `hash`, `message`, `author`, `date`, `filesChanged`, `additions`, `deletions` |
| 2 | api.getIssueCommits calls GET /issues/:number/commits | PASS | `api.ts:59-60` calls `request<...>(\`/issues/${number}/commits\`)` |
| 3 | api.getCommitDiff calls GET /issues/:number/commits/:hash/diff | PASS | `api.ts:62-63` calls `request<...>(\`/issues/${number}/commits/${hash}/diff\`)` |
| 4 | useIssueCommits hook uses queryKey ['issues', number, 'commits'] | PASS | `useQueries.ts:51-55` uses `queryKey: ['issues', number, 'commits']` |
| 5 | useCommitDiff hook accepts enabled flag for lazy loading | PASS | `useQueries.ts:57-64` accepts `enabled` param, passes as `enabled: enabled && number > 0 && !!hash` |
| 6 | Typecheck passes | PASS | `npm run build` succeeded with zero type errors |
| 7 | Build passes | PASS | `npm run build` exited 0 |

#### T-004: Changed Files tab layout and Commits view

| # | Criterion | Status | Evidence |
|---|-----------|--------|----------|
| 1 | Files and Commits (N) tabs with Files default | PASS | `IssueDetailPage.tsx:116` defaults `diffTab` to `'files'`; `IssueDetailPage.tsx:384-403` renders both tab buttons with `Commits (${commits.length})` |
| 2 | Commits list with hash, message, time, +/- stats | PASS | `IssueDetailPage.tsx:87-91` renders `commit.hash` (mono), `commit.message`, `formatTimeAgo(...)`, `+{commit.additions}`, `-{commit.deletions}` |
| 3 | Click expands diff (green/red/mono) | PASS | `IssueDetailPage.tsx:37-44` applies `bg-green-50 text-green-700` for `+` lines, `bg-red-50 text-red-700` for `-` lines, mono via `font-mono` at line 33 |
| 4 | Click again collapses | PASS | `IssueDetailPage.tsx:435-444` toggles `expandedCommits` Set (add/delete); `IssueDetailPage.tsx:93` conditionally renders diff only when `expanded` is true |
| 5 | Multiple commits expandable simultaneously | PASS | `IssueDetailPage.tsx:117` uses `Set<string>` (not single value), allowing multiple hashes to be expanded at once |
| 6 | Loading indicator shown | PASS | `IssueDetailPage.tsx:95-97` renders "Loading diff..." when `isLoading` is true |
| 7 | "Failed to load diff" shown on error | PASS | `IssueDetailPage.tsx:98-100` renders "Failed to load diff" when `isError` is true |
| 8 | "No commits yet" empty state | PASS | `IssueDetailPage.tsx:426-428` renders `<p>No commits yet.</p>` when `commits.length === 0` |
| 9 | Tab selection persists across SSE refreshes | PASS | `diffTab` is React `useState` at `IssueDetailPage.tsx:116` which persists across re-renders triggered by data refetch/SSE |
| 10 | Area hidden when both files and commits are empty | PASS | `IssueDetailPage.tsx:380` returns `null` when `files.length === 0 && commits.length === 0` |
| 11 | Typecheck passes | PASS | `npm run build` succeeded with zero type errors |
| 12 | Build passes | PASS | `npm run build` exited 0 |

## Fix Suggestions

1. **[`packages/cli/src/api/issues.ts:1293-1301`]** Remove the dead first stat match block. The second block (`issues.ts:1303-1311`) handles all cases correctly. The first block's `+`/`-` character counting is wrong and its values are always overwritten.

2. **[`packages/cli/src/api/issues.ts:1270`]** Remove `%b` from the format string. Change `'--format=%h%x00%s%x00%an%x00%aI%x00%b%x01'` to `'--format=%h%x00%s%x00%an%x00%aI%x01'`. Only the subject line is needed; the body is unused and adds noise to parsing.

3. **[`packages/cli/src/api/issues.ts:1334`]** Add hash format validation: `if (!/^[0-9a-f]{7,40}$/i.test(hash)) return c.json({ success: false, error: 'Invalid commit hash' }, 400)` before the git commands.

4. **[`tests/` — NEW FILE]** Add unit/integration tests for the two new API endpoints covering: happy path, empty results, all error cases (400/404), and stat parsing correctness. Follow the pattern in `tests/api-integration.test.ts`.
