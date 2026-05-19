## Why

The changed-files page is the final reading surface for issue work, but real active diffs can currently produce a blank direct-load route or an overwhelming all-files patch dump dominated by generated files. This change is needed now because the existing surface and merge-base semantics are in place, but the delivered reader is not reliable or readable enough for day-to-day Mohist code review.

## What Changes

- Make `/issue/:number/files` render reliably on direct URL load, browser refresh, and SPA navigation for issues with available diff data.
- Add a visible recoverable error state for route or API failures, including a path back to the issue detail page.
- Change the initial reader state so the page does not eagerly render every line of every changed file into the DOM before the user chooses what to read.
- Keep the changed-files tree visible and useful as the orientation surface, with either a sensible first non-generated file selected or a lightweight summary/empty reader prompting file selection.
- Collapse large generated files and lockfiles by default with changed-line counts and an explicit `Render anyway` action.
- Ensure large-diff protection applies consistently to the default reading flow as well as single-file, split, search, raw patch, and full-file modes where applicable.
- Remove duplicated file headers when rendering file blocks so each visible file has one clear sticky context header.
- Preserve the existing merge-base diff contract and reading-only toolbar scope.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- issue-changed-files-reader

## Impact

- Web route and page behavior: `packages/cli/web/src/App.tsx` and `packages/cli/web/src/components/IssueChangedFilesPage.tsx`.
- Changed-files reader components: `ChangedFilesTree`, `UnifiedDiffPane`, `SplitDiffPane`, `RawPatchPane`, `FullFilePane`, and `DiffSearchPane` under `packages/cli/web/src/components/issue-changed-files/`.
- Diff parsing and classification utilities: `packages/cli/web/src/lib/diffModel.ts`, including large-diff and generated/lockfile detection if kept client-side.
- Query and error handling paths for issue, diff, commit, and commit-diff data in `packages/cli/web/src/hooks/useQueries.ts` and the API client types.
- Existing issue diff APIs in `packages/cli/src/api/issues.ts` remain merge-base based; API changes should be limited to metadata needed for reliable rendering, not a semantic contract change.
- Regression coverage in `packages/cli/web/src/components/IssueChangedFilesPage.test.tsx` and related routing tests for direct load, refresh, default collapse behavior, and duplicate-header prevention.
