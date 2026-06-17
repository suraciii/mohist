## MODIFIED Requirements

### Requirement: REQ-WUI-ISSUE-MARKDOWN-001 Issue Detail renders Markdown content

Issue Detail Page SHALL render issue descriptions and comments through the shared `MarkdownReader` component instead of a page-local `react-markdown` wrapper. Markdown rendering SHALL support headings, paragraphs, line breaks, ordered and unordered lists, emphasis, strikethrough, inline code, fenced code blocks, blockquotes, horizontal rules, explicit Markdown links, and bare URL autolinks. The issue description SHALL use a base heading level greater than 1 so embedded Markdown `#` headings do not create duplicate page-level `h1` landmarks that compete with the issue page title.

#### Scenario: Description Markdown is readable

- **WHEN** a user opens an issue whose description contains Markdown headings, lists, links, bare URLs, emphasis, strikethrough, inline code, fenced code blocks, blockquotes, or horizontal rules
- **THEN** the Description section renders those structures as formatted content through `MarkdownReader`
- **AND** raw Markdown markers such as heading prefixes, list prefixes, and code fences are not shown as the primary reading experience

#### Scenario: Comment Markdown is readable

- **WHEN** a user opens an issue with comments containing Markdown formatting or code snippets
- **THEN** each comment body renders the Markdown as formatted content through `MarkdownReader`
- **AND** comment timestamps and delete actions remain available

#### Scenario: Embedded headings do not become page-level h1 landmarks

- **WHEN** a user opens an issue whose description begins with a Markdown `#` heading
- **THEN** that embedded heading SHALL NOT render as a page-level `h1`
- **AND** the issue page title remains the only page-level `h1` landmark on Issue Detail

### Requirement: REQ-WUI-ISSUE-MARKDOWN-002 Issue Detail provides readable Markdown code styling

Issue Detail Page Markdown rendering through `MarkdownReader` SHALL visually distinguish inline code and fenced code blocks while preserving the existing compact gray page styling. Inline code SHALL use a light gray background, monospaced font, rounded corners, and compact padding; fenced code blocks SHALL use a light gray background, monospaced font, rounded corners, padding, and horizontal scrolling for long lines contained inside the code block so that long code lines do not produce page-level horizontal scrolling on desktop or mobile.

#### Scenario: Inline code is visually distinct

- **WHEN** a description or comment contains inline Markdown code
- **THEN** the rendered inline code is visually distinct from surrounding prose
- **AND** it uses compact styling consistent with the page text size

#### Scenario: Code block is readable

- **WHEN** a description or comment contains a fenced code block
- **THEN** the code block renders with a distinct background and monospaced font
- **AND** long lines can be read by horizontal scrolling inside the code block without breaking the page layout

### Requirement: REQ-WUI-ISSUE-MARKDOWN-003 Issue Detail delegates long-description collapse to MarkdownReader

Issue Detail Page SHALL keep long descriptions from dominating the first screen by delegating collapse/expand behavior to `MarkdownReader` with `mode="collapsible"` instead of owning a page-local `max-h-[600px]` clip, gradient overlay, `descriptionExpanded` state, or `scrollHeight` check. The user SHALL be able to expand the description to read the full rendered Markdown and collapse it again through the Reader-level control.

#### Scenario: Long description is collapsed by default via the Reader

- **WHEN** a user opens an issue with a description longer than the collapse threshold
- **THEN** the Description section initially constrains the rendered content height to about 600px through `MarkdownReader` `collapsible` mode
- **AND** an expand control is rendered by the Reader

#### Scenario: User expands and collapses description via the Reader

- **WHEN** the user activates the expand control on a collapsed description
- **THEN** the full rendered Markdown description becomes visible
- **AND** a collapse control is available to restore the constrained view

#### Scenario: Issue Detail no longer owns collapse state

- **WHEN** the `IssueDetailPage` source is inspected
- **THEN** it does not contain `descriptionExpanded`, `descriptionBodyRef`, `scrollHeight > 600`, the page-local `max-h-[600px]` clip, or the gradient overlay
- **AND** collapse/expand behavior is delegated to `MarkdownReader`

#### Scenario: Existing issue actions still work

- **WHEN** a user edits the issue, submits a comment, or deletes a comment after Markdown rendering is enabled
- **THEN** the existing action still uses the existing API flow
- **AND** the issue detail data refresh behavior remains unchanged
