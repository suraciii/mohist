## Why

Mohist already exposes issue diffs inside Issue Detail, but that surface is optimized for issue management rather than sustained code reading, so users still have to fight page noise, flat file lists, and unstable reading context to understand what changed. Now that Files changed has become core issue evidence, the Web UI needs a dedicated reading page that makes final file-level changes easy to scan and inspect without mixing in approval, comments, or transcript-focused diffs.

## What Changes

- Add a dedicated issue changed-files reading page at `/issue/:number/files` for browsing the final base-vs-head file diff of an issue worktree.
- Keep Issue Detail lightweight by reducing its Changes area to a compact summary with a `View files` entry point into the dedicated reader.
- Upgrade the file-reading experience from a flat expandable list to a directory-grouped changed-files tree with file filtering and a stable diff reading pane.
- Add reading controls expected from a PR-style files view, including unified diff by default, expand/collapse all, sticky per-file headers, and protection against rendering very large diffs by default.
- Extend the reader toward commit-scoped and alternate diff/file views where they serve reading comprehension, while explicitly excluding approval actions, review reports, line comments, and merge decisions from this page.

## Capabilities

### New Capabilities

- `issue-changed-files-reader`

### Modified Capabilities

- `issue-review-surface`
- `web-ui`

## Impact

- `packages/cli/web/src/App.tsx` — add the new `/issue/:number/files` route within the existing project-guarded Web UI.
- `packages/cli/web/src/components/IssueDetailPage.tsx` — keep the issue-level change summary and add the lightweight `View files` navigation entry without turning Issue Detail into the primary code-reading surface.
- `packages/cli/web/src/components/ChangesPanel.tsx` and `packages/cli/web/src/components/DiffViewer.tsx` — likely refactor or split current inline diff UI so the dedicated page can reuse diff parsing/rendering while adding tree navigation, sticky headers, reader controls, and large-diff guards.
- `packages/cli/web/src/hooks/useQueries.ts`, `packages/cli/web/src/lib/api.ts`, and `packages/cli/web/src/lib/types.ts` — extend existing diff/commit query models as needed for richer file-reader state such as per-file metadata, commit-scoped reading, and alternate render modes.
- `packages/cli/src/api/issues.ts` and the existing issue diff/commit endpoints — continue serving the source data for issue changed-files reading, and may need response enrichment for file tree metadata, changed-line counts, or commit-targeted file views.
- OpenSpec specs are affected at the behavior level: this change introduces a dedicated reading capability and updates existing review-surface/UI requirements so Files changed is a first-class reader, not just an inline section on Issue Detail.
- No review comments, approval/reject actions, AI review report panels, merge actions, or transcript diff replacement are introduced by this change.
