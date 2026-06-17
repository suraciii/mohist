## Why

Mohist renders Markdown in several surfaces — issue descriptions, artifact content, review reports, and session transcripts — but there is no shared Markdown *Reader* contract. The current `MarkdownContent` is a thin `react-markdown` wrapper that only customizes code blocks; typography, heading hierarchy, table containment, long-link wrapping, code-block actions, and long-document collapse are each left to (or hard-coded by) the consuming page. As a result the four surfaces look and behave inconsistently, embedded Markdown `#` headings create duplicate page-level `h1` landmarks that compete with the page title, and long-document collapse is owned page-locally by `IssueDetailPage` as a 600px max-height + gradient overlay. The missing piece is a reusable Reader primitive that gives rendered Markdown a single consistent product-level reading experience, so this issue standardizes the contract before any more surfaces diverge.

## What Changes

- Introduce a reusable `MarkdownReader` component under shared UI on top of the existing `react-markdown` + `remark-gfm` + Tailwind typography stack. It owns document typography and spacing, base heading-level remapping (`baseHeadingLevel`), long-document modes (`full` | `collapsible` with `collapsedHeight`), optional table-of-contents, heading anchors, and copy-code affordances.
- Remap `h1`-`h6` through `react-markdown` component overrides so embedded Markdown headings can be shifted relative to a consumer-chosen base level and never produce duplicate page-level `h1` landmarks.
- Wrap tables in a horizontal-scroll container and apply `overflow-wrap: anywhere` for links, inline code, long filesystem paths, and generated identifiers so they cannot expand the page width on desktop or mobile.
- Render fenced code blocks with stable overflow handling and an optional copy-code action.
- **BREAKING (internal API):** Move the issue description expand/collapse behavior out of `IssueDetailPage` (`descriptionExpanded`, `scrollHeight > 600`, gradient overlay) and delegate it to `MarkdownReader mode="collapsible"`.
- Migrate the minimum required surfaces to `MarkdownReader`: issue description, issue comments, and artifact Markdown (the `text-sm text-gray-800` plain wrapper). Existing issue detail must no longer emit duplicate page-level `h1` landmarks from embedded Markdown.
- Keep `MarkdownContent` either as the low-level renderer consumed by `MarkdownReader` or migrate its behavior into `MarkdownReader` without gratuitously breaking other call sites. Review-report modal, session-transcript Markdown, and other direct `react-markdown` usages are explicit follow-up migration targets and are out of scope for the initial implementation unless required to keep the shared API coherent.
- Add component tests covering heading-level remapping, code overflow, table overflow, long link/path wrapping, and collapsible/full reader modes; update existing issue/artifact Markdown tests to assert the new Reader behavior.

## Capabilities

### New Capabilities

- `markdown-reader`: shared Markdown Reader presentation contract — covers the `MarkdownReaderProps` API (`content`, `baseHeadingLevel`, `mode`, `collapsedHeight`, `showToc`, `showHeadingAnchors`, `showCopyCode`, `className`), reader-level typography and spacing, heading-level remapping, table containment, code-block overflow and copy-code, long-link/long-path wrapping, and Reader-level collapsible/full long-document behavior. Issue description, comment, and artifact Markdown migrations are expressed as requirements under this capability.

### Modified Capabilities

- `web-ui`: the existing `REQ-WUI-ISSUE-MARKDOWN-001/002/003` requirements describe Markdown rendering and the 600px page-local description collapse as Issue Detail behavior. They are amended so (a) issue descriptions and comments render through `MarkdownReader` rather than a page-local renderer, (b) the page-local 600px collapse/Expand control is removed from `IssueDetailPage` and replaced by Reader-level `collapsible` mode, and (c) embedded Markdown headings SHALL NOT create duplicate page-level `h1` landmarks.

## Impact

- `packages/web/src/shared/ui/` — new `MarkdownReader` component (plus tests) and the heading-remap / table-wrapper / copy-code building blocks it composes.
- `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx` — remove the page-local `MarkdownContent`, `descriptionExpanded`, `descriptionBodyRef`, `scrollHeight > 600`, gradient overlay, and Expand/Collapse control; render issue description and comments through `MarkdownReader` with a consumer-chosen base heading level so embedded `#` no longer competes with the page title.
- `packages/web/src/widgets/issue-workflow/ui/ArtifactContentViewer.tsx` (and any other artifact Markdown rendering site using the `text-sm text-gray-800` plain wrapper) — render Markdown content through `MarkdownReader`.
- `packages/web/tests/IssueDetailPage.test.tsx` — replace the `scrollHeight` 700/300 collapse assertions with Reader-mode assertions; add coverage for the no-duplicate-h1 behavior and Reader-based collapse.
- New `MarkdownReader` component tests covering the acceptance criteria (heading remap, code overflow, table overflow, long link/path wrapping, collapsible vs full mode).
- Out of scope for this change (follow-up only): `ReviewReportModal.tsx` `prose prose-sm max-w-none` path, `SessionTranscriptView.tsx` and `AssistantParts.tsx` direct `react-markdown` usage, and any remaining direct `react-markdown` call sites. No Markdown storage, issue body schema, or backend API changes. No parser replacement unless implementation uncovers a concrete blocker in the current `react-markdown` path.
