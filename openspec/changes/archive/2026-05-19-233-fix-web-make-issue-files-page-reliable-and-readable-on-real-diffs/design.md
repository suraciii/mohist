## Context

The changed-files page is already the final code-reading surface for issue work, and the diff API already uses the merge-base semantics established by earlier changes. The current web implementation fails at the page layer: direct loads can leave the React root blank, and the default reader path renders `parsedBlocks.map(...)`, causing every file and line in the issue diff to mount before the user chooses what to read.

The implementation must preserve the existing API meaning while making `/issue/:number/files` reliable for direct URL loads, refreshes, and SPA navigation. The page should keep the file tree as the orientation surface, avoid eager DOM inflation, collapse generated or dependency-heavy diffs by default, and keep the toolbar focused on reading controls only.

## Goals / Non-Goals

**Goals:**

- Ensure `/issue/:number/files` renders a usable loading, content, empty, unavailable, or recoverable error state for every route/query outcome.
- Make the initial reader state lightweight by selecting one sensible file or showing a summary prompt instead of rendering all file patches.
- Apply large-diff and generated-file protection consistently across the default reader flow and file-specific modes.
- Keep the changed-files tree visible and useful before and after file selection.
- Remove duplicated file headers by making header ownership explicit.
- Add regression coverage for direct-route rendering, refresh-equivalent routing, default large-file collapse, and single file-header rendering.

**Non-Goals:**

- Add approval, review comments, line comments, merge actions, or workflow-stage actions to the files page.
- Change issue lifecycle, stage transitions, or completion semantics.
- Replace session transcript diffs.
- Change the `/api/issues/:number/diff` merge-base contract except for optional metadata that does not alter diff meaning.

## Decisions

### D1: Treat The Files Route As A Self-Contained Data Boundary

`IssueChangedFilesPage` should explicitly handle all issue, diff, commits, and commit-diff query states instead of relying on issue-not-found handling or implicit `undefined` behavior. The page should render a visible recoverable error card when route params are invalid or required queries fail, with a button/link back to `/issue/:number` when a number is known.

Direct-load reliability should be fixed in the route/data boundary, not by adding special navigation-only behavior. The same component path should handle browser refresh and SPA navigation. If the blank root is caused by an uncaught render-time exception, the implementation should remove the throw source and cover it with route-level tests; if needed, wrap the route element in the app's existing error-boundary pattern or add a small local boundary for this page.

**Alternatives considered:** Redirecting failed files routes to issue detail was rejected because it hides recoverable API failures and does not satisfy the requirement for a visible error state. Adding direct-load-only branching was rejected because it would keep two route behaviors and make refresh regressions likely.

### D2: Default To A Selected-File Reader Or Summary, Not An All-Files Patch Stream

The page should stop using the default `parsedBlocks.map(...)` patch stream as the initial reading mode. After diff parsing, the page should either select a sensible first file or show a summary/empty reader that prompts the user to choose from the tree. The preferred implementation is to auto-select the first non-generated, non-large, non-binary file when there is no restorable selected file; if every file is generated, large, or binary, show the lightweight summary instead.

The reader area should render only the selected file's active mode (`diff`, `raw`, `full`, or `search`) or the summary prompt. This keeps the file tree visible, makes the first viewport useful, and prevents every line of every changed file from entering the DOM on initial load.

**Alternatives considered:** Virtualizing the all-files stream was considered but rejected for this change because it preserves an all-files-first mental model, complicates sticky headers and hunk navigation, and still leaves generated files competing for the first viewport. Keeping all-files rendering but collapsing only large files was also rejected because many small files can still create a noisy initial DOM and reading surface.

### D3: Centralize Diff Classification In The Diff Model Layer

Large, generated, lockfile, and dependency-file detection should live in `diffModel.ts` or an adjacent diff utility so all reader modes and tests use the same classification. Extend the parsed file model or derive a view model with fields such as `isLarge`, `isGenerated`, `collapseReason`, and `displayPath`.

The collapse rule should include the existing changed-line threshold and path-based generated/dependency indicators, especially lockfiles such as package lockfiles. Collapsed placeholders should show the changed-line count and a `Render anyway` action. The render override should be keyed by stable file identity and should apply consistently whether the file is reached from the default selection, explicit tree selection, split view, raw patch, search, or full-file mode where rendering the full content could be expensive.

**Alternatives considered:** Duplicating path checks in every pane was rejected because it would drift quickly and make acceptance coverage brittle. Moving classification entirely to the API was rejected for now because the current client already parses and renders file blocks, and the issue does not require a server contract change.

### D4: Make File Header Rendering A Pane Option With One Owner

`UnifiedDiffPane` and `SplitDiffPane` currently render their own sticky file headers, while the all-files path also wraps each pane with another sticky header. The new design should make header ownership explicit: panes should either own the selected-file header by default, or accept a prop such as `showHeader={false}` when embedded in another file block.

Since the default all-files stream is being removed, the simplest implementation is for the selected-file reader panes to keep one header and delete the extra all-files wrapper header path. If any all-files or commit-file list rendering remains internally, it must pass `showHeader={false}` or use a shared `FileDiffHeader` component exactly once per visible file.

**Alternatives considered:** Removing headers from panes entirely was rejected because selected-file reading still needs sticky file context. Leaving duplicated headers in the obsolete all-files path was rejected because tests should prevent this regression even if a future all-files affordance is added.

### D5: Keep Commit-Scoped Reading On The Same Reader Model

Commit mode should reuse the same parsed-block classification, restored/selected-file behavior, tree selection, and large-diff placeholder logic as issue-level reading. Entering commit mode can clear the selected file or auto-select the first readable commit file using the same heuristic; exiting commit mode restores the issue-level reader state.

This keeps commit-specific logic limited to choosing the diff source and header banner. It avoids a second, subtly different rendering path that could still eagerly render all commit files or skip generated-file protection.

**Alternatives considered:** Keeping the current commit mode separate was rejected because it would require duplicate fixes and tests. Disabling commit-scoped reading for this issue was rejected because the existing UI already exposes it and the change can preserve it with shared reader logic.

## Risks / Trade-offs

[Auto-selecting a file may surprise users who expected no file to be selected] → Prefer a non-generated, non-large code file and keep the tree selection visible; if no good candidate exists, show the summary prompt instead.

[Path-based generated-file detection can misclassify project-specific files] → Keep the rule conservative, document it in tests, and always provide `Render anyway`.

[Client-side parsing still requires receiving and parsing the full diff payload] → This change targets DOM/render reliability and reading comfort; API pagination or server-side summaries can be considered later if payload size becomes the bottleneck.

[Session storage may restore a file that is now generated, large, or missing] → Validate restored paths against current parsed blocks; if missing, fall back to the first-readable heuristic, and if large/generated, show the collapsed placeholder rather than rendering it immediately.

[Adding an error boundary could mask defects if too broad] → Keep the boundary local to the route or render an explicit query error state first, and assert visible recovery UI in tests.

## Migration Plan

1. Add or update diff classification helpers in the client diff model and cover generated, lockfile, large, binary, and normal code files.
2. Refactor `IssueChangedFilesPage` to derive a reader view model from the active diff source and route/query state.
3. Replace the default all-files patch stream with selected-file or summary rendering while keeping `ChangedFilesTree` visible.
4. Apply the shared collapse placeholder and `Render anyway` override to selected-file diff, split, raw, search, and full-file paths where applicable.
5. Remove duplicate file-header rendering by deleting the obsolete wrapper header or adding an explicit pane header option.
6. Add regression tests for direct-route rendering, refresh-equivalent initial routing, visible query error recovery, default non-eager rendering, generated/lockfile collapse, render override, and no duplicated file headers.
7. Verify with the web test suite and a manual browser check against an active issue with a large lockfile-heavy diff.

Rollback is limited to the web client. If the reader refactor causes problems, revert the page/component changes without changing the issue diff API or merge-base semantics.

## Open Questions

- Should the initial state auto-select the first readable file or always show a summary prompt? This design prefers auto-selection when there is a clearly readable non-generated file, with summary as the fallback.
- Should generated-file classification remain entirely client-side, or should the API eventually return file metadata to avoid duplicating classification across clients?
