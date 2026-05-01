## Context

The Issue Detail page's Changes section has two tabs (Files, Commits) defaulting to Files. The `/diff` API uses `git diff --stat` with inaccurate symbol counting for additions/deletions. The `/commits` API already runs `git log --stat` but discards per-file names, returning only aggregate counts. The commit expanded view uses an inline `CommitDiffView` that renders raw diff lines without line numbers or file grouping. A full-featured `DiffViewer` component already exists but is not wired into IssueDetailPage. Changes section is gated to `build`/`check`/`done` stages only.

Key files:
- `packages/cli/src/api/issues.ts` — `/diff` (line 1325), `/commits` (line 1461), `/commits/:hash/diff` (line 1554)
- `packages/cli/web/src/components/IssueDetailPage.tsx` — `CommitDiffView` (line 47), `CommitRow` (line 75), Changes section (line 463), `DIFF_STAGES` (line 32), `diffTab` state (line 136)
- `packages/cli/web/src/components/DiffViewer.tsx` — existing component with `parseDiff`, `FileEntry`, line numbers
- `packages/cli/web/src/lib/types.ts` — `DiffFile` (line 106), `CommitEntry` (line 112)
- `packages/cli/web/src/hooks/useQueries.ts` — `useIssueDiff`, `useIssueCommits`, `useCommitDiff`
- `packages/cli/web/src/lib/api.ts` — `getIssueDiff`, `getIssueCommits`, `getCommitDiff`

## Goals / Non-Goals

**Goals:**
- Make Commits the default tab so users see agent work narrative first
- Show file names per commit without expanding diff
- Replace CommitDiffView with DiffViewer for consistent, high-quality diff rendering
- Upgrade diff API to precise `--numstat` stats with per-file diff content
- Expand Changes visibility to stages where commits may exist
- No noise filtering — all commits shown

**Non-Goals:**
- Pagination or lazy loading for large commit lists
- File-level search/filter in Files tab
- Commit author filtering or date range filtering
- WebSocket-based real-time commit streaming

## Decisions

### D1: Reuse DiffViewer as-is for both commit expand and Files tab

`DiffViewer` already parses unified diff into `FileBlock[]` with line numbers, file grouping, expand/collapse, and binary file handling. It accepts a single `diff: string` prop. Use it directly — no modifications needed.

**For commit expand:** Pass `diffData.diff` from `useCommitDiff` to `<DiffViewer diff={...} />`. This replaces `CommitDiffView`.

**For Files tab:** The upgraded `/diff` API returns per-file diff content. Extract each file's `diff` string and render it via `<DiffViewer diff={file.diff} />` when expanded. Since `DiffViewer` parses multi-file diffs and `file.diff` is already a single file's diff, this works naturally.

**Alternatives considered:**
- Creating a new `SingleFileDiffViewer` variant — rejected because `DiffViewer` already handles single-file diffs (it parses `FileBlock[]` from any unified diff text)
- Modifying `DiffViewer` to accept pre-parsed blocks — rejected, adds complexity with no benefit

### D2: Two git commands for diff API instead of parsing unified diff for stats

Run `git diff <base>...<branch> --numstat` for precise per-file stats and `git diff <base>...<branch>` for the full unified diff. Correlate the two outputs by file path.

**Rationale:** `--numstat` gives exact integer counts (`42\t7\tpath`) while unified diff `+`/`-` line counting would miss rename/copy handling and binary file edge cases. Running two git commands is a clean separation — stats from numstat, content from diff.

**Parsing strategy:**
1. Parse `--numstat` output: each line is `additions\tdeletions\tpath` (binary files show `-\t-\tpath`)
2. Parse full diff output: split on `diff --git` boundaries into per-file diff strings
3. Match by file path to build the response array

**Alternatives considered:**
- Parse unified diff for stats — fragile, doesn't handle binary files
- Use `--stat` with wider width (`--stat=1000`) — still symbol counting, not precise
- Single `git diff --numstat --patch` — possible but mixing stat and patch output complicates parsing

### D3: Parse per-file names from existing `--stat` output in commits API

The commits API already runs `git log --format=... --stat`. The `--stat` output includes per-file lines like ` src/foo.ts | 5 ++---`. Parse these lines to extract file paths.

**Regex:** `/^\s+(.+?)\s*\|/` matches the file path before the `|` separator in stat lines. Filter out the summary line (contains "files changed").

**Alternatives considered:**
- Switch to `--name-only` — would require a second `git log` invocation or complex format mixing
- Use `--format=... --name-only` — `--name-only` doesn't combine cleanly with `--format` delimiters in all git versions

### D4: Expand DIFF_STAGES to include all post-explore stages

Change `DIFF_STAGES` from `{build, check, done}` to `{explore, plan, build, check, done}`. After `explore`, a worktree exists and may have commits. The existing guard (`files.length === 0 && commits.length === 0 → return null`) handles the empty state gracefully.

**Excluded:** `backlog` stage — no worktree exists, no commits possible.

### D5: File list truncation at 5 files on commit rows

When `commit.files.length > 5`, show first 5 file names with `+N more` indicator. The full list is visible when the commit is expanded (alongside the diff). This keeps commit rows compact for scanability while not hiding information.

## Risks / Trade-offs

- [Large diffs may slow API response] → The `/diff` API now returns full diff content. For branches with thousands of lines changed, this could be 1-2MB of text. Mitigation: this is an admin/audit tool, not a public API. Can add truncation or pagination later if needed.
- [Two git commands in diff API doubles exec time] → Both `--numstat` and full diff run against the same refs. Git's object cache makes the second command fast. Acceptable latency for an audit page.
- [Backward compatibility of API response shapes] → Both APIs add new fields without removing existing ones. `DiffFile` gains `diff` and `isBinary`; `CommitEntry` gains `files`. Old clients ignore unknown fields.

## Migration Plan

1. Backend changes first (API upgrades are backward-compatible — new fields added, none removed)
2. Frontend types updated to match new API shapes
3. Frontend UI changes (default tab, DiffViewer wiring, file list display)
4. Remove `CommitDiffView` function from IssueDetailPage
5. No database migration needed — all data comes from git commands

## Open Questions

None.
