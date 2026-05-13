## Context

Mohist currently exposes issue changes in two places: a compact summary near the top of `IssueDetailPage` and an inline `ChangesPanel` farther down the page. That is enough to prove that diff data exists, but not enough to support sustained reading across many files. The current `DiffViewer` couples patch parsing, per-file collapse state, and rendering into one component, while `IssueDetailPage` owns additional changes state (`diffTab`, `expandedFiles`, `expandedCommits`) that is specific to the embedded panel rather than a dedicated reading workflow.

The backend already exposes the main primitives needed for a first version: `GET /api/issues/:number/diff` returns a base-vs-head file list with per-file unified patches, `GET /api/issues/:number/commits` returns commit metadata, and `GET /api/issues/:number/commits/:hash/diff` returns a commit patch. Those endpoints currently optimize for direct rendering, not for a richer reader model, so the design should preserve them as the source of truth while allowing light response enrichment where the reader would otherwise have to duplicate parsing or heuristics.

The primary constraint is to improve code-reading ergonomics without turning this page into a review system. The page must stay focused on browsing final file changes, reuse existing diff data paths where possible, and avoid adding a second independent diff implementation for transcript/tool-call diffs.

## Goals / Non-Goals

**Goals:**

- Introduce a dedicated `/issue/:number/files` page that is optimized for browsing changed files and reading diffs.
- Keep `IssueDetailPage` lightweight by reducing its Changes area to summary and navigation, instead of embedding the primary reading experience.
- Normalize diff data into a reusable file-diff model that can power unified diff, split diff, sticky file headers, hunk navigation, and large-diff protection.
- Provide a left-side file tree with directory grouping and text filtering, plus a stable right-side reading pane.
- Preserve existing availability semantics for not-started issues, removed worktrees, missing branches, and git failures.
- Support phased delivery: P0 must work from the existing issue-level diff API, while P1/P2 can extend data shape in a controlled way.

**Non-Goals:**

- No review comments, line comments, approval/reject actions, merge actions, or AI review report panels.
- No replacement of transcript/tool diff presentation in session pages.
- No persistent server-side diff snapshots.
- No introduction of a generalized source browser for arbitrary repository files outside the issue diff context.

## Decisions

### D1: Build a dedicated page instead of continuing to expand `IssueDetailPage`

The changed-files reader will be implemented as a new page component mounted at `/issue/:number/files`, with its own layout, view state, and loading/unavailable states. `IssueDetailPage` will keep only a compact summary card and a `View files` entry point. This keeps the reading workflow isolated from issue description, comments, tasks, and session surfaces, which are useful in issue management but noisy during code reading.

This also keeps page-specific complexity out of `IssueDetailPage`, which is already responsible for pipeline controls, comments, branch state, questions, tasks, and session links. The changed-files reader should become the deep module for file-reading concerns; `IssueDetailPage` should remain a coordinator.

**Alternatives considered:** Continue enhancing `ChangesPanel` in place inside `IssueDetailPage`. Rejected because it would keep routing, reading state, and large-diff UX coupled to an issue-management page and make later features like scroll restoration and commit-mode reading harder to reason about.

### D2: Split diff parsing from diff presentation

The current `DiffViewer` parses raw patch text and immediately renders collapsible file blocks. The new design will pull parsing into a separate diff-model utility module that produces a normalized representation such as:

- file identity and status
- additions/deletions and binary flag
- ordered hunks
- line-level metadata for unified and split rendering
- derived counts such as changed line count and hunk count

Presentation components will then consume that normalized model. A page-level reader component can own expansion state, active file selection, large-diff suppression, view mode, and hunk navigation without re-parsing on each interaction. The existing embedded diff use cases can keep a simpler wrapper component that uses the same parser but preserves current lightweight behavior.

This moves complexity downward: parsing rules live in one place, while UI components stay focused on layout and interaction.

**Alternatives considered:** Keep `DiffViewer` as the only diff abstraction and add props for tree mode, sticky headers, split mode, hunk navigation, and large-diff protection. Rejected because it would create a shallow "do everything" component with too many interaction responsibilities and hidden state transitions.

### D3: Use the issue diff API as the primary data source, with targeted response enrichment instead of a brand-new reader API

P0 will continue to use `GET /api/issues/:number/diff` as the main source of truth for final issue changes. This keeps the backend contract aligned with the current `issue-review-surface` behavior and avoids creating a second API that computes the same base-vs-head diff. The reader will derive directory grouping and filter behavior client-side from the file list.

The API may be lightly enriched where the UI would otherwise need to re-parse raw patch text or guess semantics repeatedly. Candidate additive fields include:

- file status (`added`, `modified`, `deleted`, `renamed`)
- changed line count per file
- optional old/new path fields for rename handling

For P1 commit-scoped reading, the page can switch data sources when a commit is selected: issue-level file mode continues to use `/diff`, while commit mode uses `/commits` plus `/commits/:hash/diff`. This keeps the API model simple: one endpoint for final issue state, one for commit history, one for commit patch details.

**Alternatives considered:** Add a new `/issues/:number/files` reader-specific endpoint that returns a fully normalized tree and diff model. Rejected for now because it duplicates derivable UI structure and would bake presentation-specific hierarchy into the server too early.

### D4: Keep navigation state in the page and persist only reader-facing state needed to resume reading

The changed-files page will own state for:

- selected left-pane mode (`changes` first; `all` can arrive later)
- file filter text
- diff view mode (`unified` default, `split` later)
- expanded/collapsed files
- large-diff overrides (`Render anyway` per file)
- active hunk index / selected file for navigation

Only user-resume state that matters across navigation should be persisted, likely in `sessionStorage` keyed by issue number and reader mode. That persisted state should include selected file, scroll position anchor, and view mode, but not transient fetch state. Restoring by file path and nearest hunk anchor is more stable than restoring raw pixel offset alone because the rendered height changes when files are expanded or a large diff is unhidden.

**Alternatives considered:** Keep all state ephemeral. Rejected because one of the stated goals is to let users return to the same reading position from Issue Detail. Persisting the full parsed diff model was also considered and rejected because it duplicates query cache responsibility and risks stale state.

### D5: Large-diff protection is a rendering policy, not an availability state

Large diffs should still be reported as available by the API and counted in the summary, but the page should not eagerly render all their lines. Each file will be evaluated against a client-side threshold based on parsed changed-line count, with a placeholder card showing that the diff is hidden and a `Render anyway` action for explicit opt-in.

This is intentionally separate from API availability states like `worktree_removed` or `branch_missing`. The user can still see that the file changed and decide to expand it; the system is protecting reading rhythm and browser performance, not claiming the diff is unavailable.

The threshold should live in a small shared constant near the reader so it can be tuned without changing backend contracts.

**Alternatives considered:** Hide large diffs server-side or truncate the patch in the API. Rejected because it would make the data contract lossy and complicate future raw/full-file modes.

### D6: Use a single reader shell that swaps inner content modes instead of separate pages for issue, commit, raw, and full-file reading

The page will use one shell with a consistent left tree and right reading pane. The left side owns file selection; the right side can switch among content modes:

- diff
- raw patch
- full file (P2)

Likewise, the data source can remain issue-level or switch to commit-level without changing the route shape. This keeps the interface deep: callers navigate to one page, and the page hides the combinatorial complexity of issue-vs-commit plus diff-vs-raw-vs-full-file.

For P2 full-file mode, a new backend endpoint may be needed to fetch blob contents for base/head versions of a changed file. That should be added only when the UI actually needs it, not upfront in P0.

**Alternatives considered:** Separate routes or tabs that each own their own fetch and render logic. Rejected because it would duplicate selection, scroll restoration, and unavailable-state handling.

### D7: Keep the current embedded Changes panel simple and avoid forcing full reader capabilities into it

`ChangesPanel` should remain a compact issue-detail companion for summary and lightweight evidence, not the primary place where all future reading features accumulate. The shared parser/model utilities can be reused there, but sticky headers, hunk jumping, file-tree layout, and scroll restoration belong only to the dedicated page.

This reduces the chance that the project ends up maintaining two almost-identical but slightly divergent review surfaces.

**Alternatives considered:** Make `ChangesPanel` and the new page share the same full component tree with feature flags. Rejected because the embedded and dedicated surfaces have different complexity budgets and layout constraints.

## Risks / Trade-offs

- [Diff model refactor can break existing inline diff rendering] → Keep a compatibility wrapper around the current `DiffViewer` behavior and migrate embedded call sites to the shared parser incrementally.
- [Client-side parsing of large patches can still be expensive] → Parse once per fetched diff payload, memoize by query result, and avoid rendering hidden large-diff bodies until the user opts in.
- [Scroll restoration can feel wrong if expansion state changes] → Persist file-path and hunk/file anchors in addition to coarse scroll position, and restore after expansion state is rehydrated.
- [Issue-level file tree may need rename semantics that current API does not expose] → Treat status as additive API enrichment; if rename metadata is absent, fall back to path-only rendering in P0.
- [Commit-mode reading can complicate the shell too early] → Keep commit mode behind a clear page state boundary and reuse the same reader components; do not mix issue-level and commit-level content in one pane simultaneously.
- [A dedicated page increases surface area to test] → Keep the data flow shallow: existing queries remain authoritative, and most new tests can focus on reader state, unavailable states, and diff rendering behavior.

## Migration Plan

1. Refactor diff parsing into a reusable utility and keep the existing `DiffViewer` working through that utility.
2. Add the new changed-files page route and page shell with top summary, unavailable states, and issue-level unified diff rendering.
3. Add the left-side directory tree, filtering, sticky file headers, expand/collapse all, and large-diff suppression for P0.
4. Change `IssueDetailPage` to show the compact summary plus `View files` navigation entry instead of relying on the embedded panel as the primary reading surface.
5. Verify that existing `ChangesPanel` behavior still works for lightweight inline viewing.
6. Add P1 enhancements in place: split mode, hunk navigation, scroll restoration, and optional commit-scoped reading.
7. Add P2 enhancements only if needed by the accepted specs: full-file mode, raw mode polish, and in-diff search.

Rollback strategy:

- If the dedicated page proves unstable, remove the `/issue/:number/files` route and `View files` entry while retaining the parser refactor and existing embedded `ChangesPanel` behavior.
- Because the initial design reuses existing diff endpoints, rollback is primarily a frontend route/component removal rather than a backend schema rollback.

## Open Questions

- Should the left-pane `All` view be shipped in the first implementation if the API only returns changed files, or should P0 ship only `Changes` and reserve `All` for a later endpoint that can enumerate repository files from the retained worktree?
- For P2 full-file mode, should the backend return the head version only, or both base and head blobs so changed lines can be highlighted against exact diff context without re-diffing in the browser?
- Do we want commit mode to reuse the same route with in-page selection state, or introduce a query parameter such as `?commit=<sha>` for shareable deep links once that feature lands?
