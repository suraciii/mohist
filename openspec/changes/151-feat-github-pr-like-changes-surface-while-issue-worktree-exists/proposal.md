## Why

Mohist users need a reliable PR-like review surface on Issue Detail so approvals and re-reviews can be based on the actual file diff and commit history while an issue worktree still exists. The current Changes view can report incomplete commit data and ambiguous empty states, making active issues with real work appear as if they have no meaningful changes.

## What Changes

- Add an issue review summary that exposes base branch, issue branch, files changed, commit count, diffstat, worktree availability, and merge truth in the Issue Detail review context.
- Make Files changed the default Changes view, with a complete file list and expandable per-file diffs using the existing diff viewer.
- Keep Commits as a companion view that shows the complete base-to-head commit range with hash, message, author/time, touched files, and additions/deletions.
- Allow each commit to expand into its patch diff without introducing a new external diff rendering dependency.
- Upgrade diff and commit API responses to carry explicit availability semantics instead of returning indistinguishable empty arrays for removed worktrees, missing branches, not-started issues, or git errors.
- Fix the commits API parsing so `git log --stat` output cannot collapse or drop commits from multi-commit issue branches.
- Replace ambiguous Changes empty states with cause-specific states such as no changes yet, no commits yet, workspace removed, branch missing, and failed to load changes.
- Preserve the lifecycle boundary: diff and commits are required while the issue worktree exists, but are not persisted or required after the worktree is removed.

## Capabilities

### New Capabilities

- `issue-review-surface` — PR-like Changes review surface for issue worktrees, including review summary, files-first diff review, commit history, commit patch expansion, and lifecycle-aware unavailable states.

### Modified Capabilities

- `http-api` — diff, commits, and commit-diff endpoints expose complete base/head review data and explicit availability reasons instead of shallow empty responses.
- `web-ui` — Issue Detail presents Changes as a primary review surface with files-first default behavior, complete summaries, and precise empty/unavailable states.

## Impact

- `packages/cli/src/api/issues.ts` — update `GET /api/issues/:number/diff`, `GET /api/issues/:number/commits`, and `GET /api/issues/:number/commits/:hash/diff` to report availability, base/head metadata, summary counts, complete file/commit data, and robust git parsing.
- `packages/cli/web/src/components/IssueDetailPage.tsx` — surface review summary data near the top of Issue Detail and default the Changes area to review evidence rather than process information.
- `packages/cli/web/src/components/ChangesPanel.tsx` — render files-first PR-like review UI, commit list metadata, expandable commit patches, and lifecycle-aware empty states.
- `packages/cli/web/src/components/DiffViewer.tsx` — continue as the shared renderer for file and commit diffs; no new diff rendering dependency is expected.
- `packages/cli/web/src/hooks/useQueries.ts`, `packages/cli/web/src/lib/api.ts`, and `packages/cli/web/src/lib/types.ts` — update query, client, and type shapes for availability-aware diff/commit responses.
- `WorktreeManager` and git branch/worktree checks — remain the source of truth for whether review data is available; removed worktrees intentionally end the review surface lifecycle.
- No persistent patch snapshot, remote PR integration, review comments, or AI review report mixing is introduced by this change.
