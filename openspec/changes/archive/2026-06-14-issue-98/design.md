## Context

Workflow artifacts (`proposal.md`, `design.md`, `review.md`, etc.) are the primary deliverables users care about when reviewing an issue. Currently, the web UI buries this information in two ways:

- **Discoverability**: `WorkflowView.tsx` already receives `artifactSummaries` for each task and renders a `TaskArtifactSummaryChip` component, but only inside the expanded task detail panel. A user must expand every completed task to learn which artifacts exist.
- **Readability**: `ArtifactContentViewer.tsx` renders all text artifacts with a single `<pre className="font-mono">` block. Markdown deliverables appear as raw source, making them hard to read.

The required data already exists in the API (`WorkflowArtifactSummary`, `WorkflowArtifactDirectory.totalSize`, per-file `size`). This change is therefore a UI-only presentation improvement. The project already depends on `react-markdown` and `remark-gfm` (used by `IssueDetailPage.tsx` for issue descriptions), so no new dependencies are required.

## Goals / Non-Goals

**Goals:**

- Surface clickable artifact chips directly on completed task rows in `WorkflowView.tsx`.
- Render `.md` / `.markdown` artifacts as formatted markdown in `ArtifactContentViewer.tsx`, matching the existing `IssueDetailPage.MarkdownContent` pattern.
- Keep non-markdown text artifacts (`.json`, `.yaml`, `.txt`, etc.) as monospaced `<pre>` output.
- Display artifact file size in the viewer header, plus aggregate file count and total size for directory artifacts.
- Show transient "Copied" feedback when the user copies artifact content.
- Preserve existing directory browsing and artifact-history behavior.

**Non-Goals:**

- Adding artifact download actions.
- Adding artifact search, filtering, or version comparison.
- Showing real-time artifact generation status.
- Changing backend API or data models.

## Decisions

### 1. Reuse the existing `TaskArtifactSummaryChip` on the task row

**Decision:** Move/render the existing `TaskArtifactSummaryChip` inside `TaskItem`'s row, between the task title and the duration, gated by `task.status === 'completed'` and `artifactSummaries.length > 0`.

**Rationale:** The component already implements the requested pill style (`bg-blue-50 text-blue-700`), file/directory icons, and click handling. Reusing it avoids duplication and keeps the expanded panel and row chips consistent.

**Alternative considered:** Create a separate `TaskRowArtifactChip` component. Rejected because the existing chip already satisfies the visual and interaction requirements; splitting would only add maintenance overhead.

### 2. Keep chips visible only for completed tasks

**Decision:** Show row chips only when `task.status === 'completed'` and `artifactSummaries.length > 0`.

**Rationale:** Matches the spec's visibility rule and prevents distracting or misleading chips on running/failed tasks whose artifacts may still be incomplete or missing.

### 3. Extract a shared `MarkdownContent` component

**Decision:** Move the `MarkdownContent` renderer (currently local to `IssueDetailPage.tsx:100-125`) to a shared location (e.g., `packages/web/src/shared/ui/components/markdown-content.tsx` or `packages/web/src/shared/ui/markdown-content.tsx`) and import it from both `IssueDetailPage.tsx` and `ArtifactContentViewer.tsx`.

**Rationale:** Promotes consistency and prevents drift between issue-description markdown and artifact markdown. The current inline component is exactly the pattern requested by the issue.

**Alternative considered:** Copy the `MarkdownContent` implementation into `ArtifactContentViewer.tsx`. Rejected because it duplicates the GFM plugin setup, inline-code styling, and code-block styling; a shared component is cleaner.

### 4. Markdown detection by file extension

**Decision:** Detect markdown with `path.toLowerCase().endsWith('.md') || path.toLowerCase().endsWith('.markdown')`.

**Rationale:** Simple, matches the spec, and avoids relying on `contentType`, which may not be reliable for all artifacts. Case-insensitive comparison makes the check robust.

### 5. File and directory size formatting

**Decision:** Continue using the existing `formatBytes` helper in `ArtifactContentViewer.tsx`. Extend the header to show:

- For file artifacts: artifact name/path + size.
- For directory artifacts: directory name + `(N files · totalSize)`.
- For a selected directory entry: the existing entry path + size breadcrumb remains, and the top-level directory aggregate is no longer needed while browsing a file.

**Rationale:** The helper is already present and consistent with the directory browsing UI. Placing size next to the title provides the at-a-glance info requested without adding new UI elements.

### 6. "Copied" feedback via `navigator.clipboard` + transient state

**Decision:** Add a small copy button to `ArtifactContentViewer.tsx` (e.g., in the header or above the content). On click, copy `data.content` to the clipboard and render a "Copied" indicator that clears after a short timeout (≈2 s) or when the viewer closes.

**Rationale:** The spec asks for feedback "when user selects/copies content." Implementing an explicit copy button is the most reliable way to detect the copy action across browsers. Using `navigator.clipboard.writeText` is the modern standard.

**Alternative considered:** Listen to the global `copy` event inside the dialog. Rejected because it is harder to scope to the artifact content and gives no feedback if the user copies only part of the content.

## Risks / Trade-offs

- `[Risk]` Rendering large markdown artifacts with `react-markdown` could be slow or block the main thread for very large files.
  `-> Mitigation`: Keep the content rendering path identical to `IssueDetailPage`, which already handles typical issue descriptions. If size becomes a problem in the future, add a virtualized or "render first N bytes" fallback; out of scope here.

- `[Risk]` Changing the shared markdown component impacts `IssueDetailPage` styling.
  `-> Mitigation`: Move the component verbatim and verify issue descriptions still render correctly. Avoid changing the component's props or default styling in this change.

- `[Risk]` Chips on the task row may overflow on narrow viewports.
  `-> Mitigation`: Use `truncate` on chip text and `flex-wrap`/`shrink-0` utilities so chips wrap or ellipsize gracefully. The existing chip already uses `truncate`.

- `[Risk]` The copy button fails in insecure contexts where `navigator.clipboard` is unavailable.
  `-> Mitigation`: Gracefully degrade by catching the rejection and showing a fallback message such as "Unable to copy" or falling back to `document.execCommand('copy')`.

## Migration Plan

No deployment or data migration steps are required. The change is purely client-side UI:

1. Move `MarkdownContent` to a shared module.
2. Update `IssueDetailPage.tsx` to import the shared component.
3. Update `WorkflowView.tsx` to render `TaskArtifactSummaryChip` on the task row for completed tasks.
4. Update `ArtifactContentViewer.tsx` to:
   - import and conditionally render `MarkdownContent` for markdown paths;
   - keep `<pre>` rendering for non-markdown text;
   - show size in the header;
   - add a copy button with "Copied" feedback.
5. Run the project's lint/typecheck commands.
6. Manually verify a completed task with `proposal.md` and a directory artifact.

Rollback: revert the three modified files and the new shared component file.

## Open Questions

- Should the copy button appear for directory artifact listings as well, or only when viewing a file's text content? (Initial implementation: only for text content, since directory listings are UI lists rather than raw content.)
- Should markdown detection also consider the artifact's `contentType` as a secondary signal? (Initial implementation: extension-only, matching the spec.)
