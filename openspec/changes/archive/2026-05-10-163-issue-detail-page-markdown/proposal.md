## Why

Issue Detail Page currently renders descriptions and comments as plain pre-wrapped text, making Markdown-heavy issue specifications and code discussions difficult to scan and read. This directly affects core issue comprehension now that issues are used for structured specs, agent output, links, and code snippets.

## What Changes

- Render Issue Detail Page descriptions as Markdown instead of raw plain text, including GitHub-flavored Markdown constructs needed for strikethrough and bare URL autolinks.
- Render issue comments as Markdown so formatted user and agent comments remain readable.
- Provide clear inline code and fenced code block styling consistent with the existing session transcript Markdown rendering.
- Collapse long issue descriptions by default when they exceed the page readability threshold, with an explicit expand/collapse control.
- Preserve existing issue actions and comment workflows, including edit, comment submission, and comment deletion.

## Capabilities

### New Capabilities


### Modified Capabilities

- web-ui

## Impact

- Affects `packages/cli/web/src/components/IssueDetailPage.tsx`, specifically the Description and Comments rendering paths.
- Reuses the existing `react-markdown` dependency from `packages/cli/web/package.json`; may add `remark-gfm` if needed for strikethrough and bare URL autolink support.
- Should align Markdown code styling with the existing pattern in `packages/cli/web/src/components/SessionTranscriptView.tsx`.
- No API, database, or issue provider changes are expected.
- Existing edit and comment mutations should remain unchanged because only read-side rendering behavior changes.
