## Why

Workflow artifacts such as `proposal.md`, `design.md`, and `review.md` are the primary deliverables users care about, but the current UI buries them inside expanded task details and renders markdown as raw `<pre>` text. Improving artifact discoverability and readability will let users understand progress and review deliverables at a glance without expanding every task.

## What Changes

- **Task row artifact chips**: Completed workflow tasks with `artifactSummaries` show clickable artifact chips directly on the task row, using file/directory icons and subtle pill styling.
- **Markdown artifact preview**: `ArtifactContentViewer` detects `.md` / `.markdown` files and renders them with the same `react-markdown` + `remarkGfm` pattern used by issue description markdown; non-markdown files remain monospaced `<pre>` text.
- **Artifact viewer header enhancements**: The viewer header displays the artifact file size, shows "Copied" feedback after the user copies content, and directory artifacts show total file count and total size.

## Capabilities

### New Capabilities

- `workflow-artifact-ux`: Surface workflow artifacts on task rows and improve the artifact content viewing experience with markdown rendering, size information, and copy feedback.

### Modified Capabilities

- *(none — the required data model fields `artifactSummaries`, `size`, and `totalSize` already exist; this change is purely UI/UX presentation.)*

## Impact

- Affected UI: `packages/web/src/widgets/issue-workflow/ui/WorkflowView.tsx` (task rows), `packages/web/src/widgets/issue-workflow/ui/ArtifactContentViewer.tsx` (viewer dialog).
- Reuses existing markdown components and styling from `IssueDetailPage.tsx`.
- No backend API or data model changes required.
