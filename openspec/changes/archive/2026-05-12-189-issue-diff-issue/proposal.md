## Why

Issue Detail diff review is currently untrustworthy for branches that regularly merge the base branch, because Mohist compares from the old merge-base instead of comparing the current base branch worktree to the issue branch worktree. This causes reviewers to see already-merged archive and spec changes from other issues as if they belong to the current issue, so the review surface needs to match GitHub-style PR expectations now that Changes is a primary approval surface.

## What Changes

- Change issue diff generation from three-dot comparison to two-dot comparison so issue review only shows changes unique to the issue branch relative to the current base branch.
- Apply the same comparison semantics to both the issue diff API and the `mo issue diff` CLI command so Web UI and terminal review stay consistent.
- Keep diff summary and `--numstat` output aligned with the cleaned comparison range so file counts and additions/deletions reflect only the issue's own changes.
- Preserve commit-level diff behavior for individual commits, which already uses commit-specific git inspection and is not affected by this fix.
- Preserve the current behavior when an issue branch is behind the base branch, and document that two-dot comparison can expose that state as base-only changes unless the branch is merged forward first.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `issue-review-surface`
- `http-api`

## Impact

- `packages/cli/src/api/issues.ts` - `GET /api/issues/:number/diff` and its `--numstat` summary need to switch from three-dot to two-dot diff semantics so the API returns only issue-owned file changes.
- `packages/cli/src/cli/commands/issue.ts` - `mo issue diff <number>` needs the same comparison change so CLI output matches the Issue Detail review surface.
- `packages/cli/web/src/components/ChangesPanel.tsx` and `packages/cli/web/src/components/DiffViewer.tsx` are affected behaviorally because they will render a smaller, cleaner diff payload without needing UI-specific rendering changes.
- `openspec/changes/151-feat-github-pr-like-changes-surface-while-issue-worktree-exists/specs/issue-review-surface/spec.md` and the corresponding `http-api` delta will need requirement updates to define the base-vs-head comparison semantics for issue-level diff review.
- No new dependencies, persistence changes, or commit-diff endpoint changes are expected.
