## Why

The Issue Detail page's Files and Commits tabs cannot provide meaningful code review. The Commits API is broken (returns 1 of 6 commits, all stats +0/-0 — Issue #90), and the Files tab only shows inaccurate `+/-` symbol counts without actual diff content. Users have no way to review code changes inline, unlike GitHub PR's Files changed view. Merging both tabs into a unified Changes tab with GitHub PR-style inline diff review fixes this gap.

## What Changes

- Merge Files + Commits tabs into a single **Changes** tab with two sub-views: Files changed (default) and Commits
- Replace the existing `/diff` API's inaccurate symbol-counting with `git diff --numstat` for precise stats and `git diff` for full unified diff content
- Add a **DiffViewer** component that parses unified diff into line-level rendering with standard green (addition) / red (deletion) / gray (context) highlighting
- Add commit noise filtering: `chore(tasks)`, `WIP`, `chore: commit remaining` commits default-collapsed under an "Auto commits (N)" group
- Add file-level search/filter in Files changed view
- Binary files display "Binary file, no diff available"
- No worktree → "No changes yet" empty state
- Existing `/commits` and `/commits/:hash/diff` endpoints remain unchanged (no breaking changes)
- **Depends on Issue #90** being fixed first (Commits API parsing bug)

## Capabilities

### New Capabilities

- `diff-viewer`: Frontend DiffViewer component — parses unified diff into line-level rendering with green/red/gray highlighting, file-level expand/collapse, and binary file handling
- `changes-tab`: Unified Changes tab on Issue Detail page — merges Files + Commits into two sub-views (Files changed default, Commits list) with commit noise filtering and file search

### Modified Capabilities

- `http-api`: Extend `GET /api/issues/:number/diff` response to include precise `numstat`-based additions/deletions counts and full unified diff content per file (backward-compatible, adds new fields)
- `web-ui`: Issue Detail page tab layout changes — Files + Commits tabs replaced by single Changes tab with sub-view switching
- `issue-commits-view`: Commit list view gains noise filtering (auto-collapsing chore/WIP commits)

## Impact

- **Backend** (`packages/cli/src/api/issues.ts`): `/diff` route reworked to use `git diff --numstat` + `git diff` instead of `--stat` symbol counting
- **Frontend** (`packages/cli/web/src/components/IssueDetailPage.tsx`): Tab structure change, new Changes tab container
- **New component** (`packages/cli/web/src/components/DiffViewer.tsx`): Unified diff parser + renderer
- **Data layer** (`packages/cli/web/src/hooks/useQueries.ts`, `packages/cli/web/src/lib/types.ts`): Diff query adapts to new API response shape with per-file diff content
- **Dependency**: Issue #90 must be resolved first for Commits sub-view to display correct data
