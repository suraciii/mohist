## MODIFIED Requirements

### Requirement: REQ-WUI-ISSUE-MARKDOWN-001 Issue Detail renders Markdown content

Issue Detail Page SHALL render issue descriptions and comments as Markdown instead of raw pre-wrapped plain text. Markdown rendering SHALL support headings, paragraphs, line breaks, ordered and unordered lists, emphasis, strikethrough, inline code, fenced code blocks, blockquotes, horizontal rules, explicit Markdown links, and bare URL autolinks.

#### Scenario: Description Markdown is readable

- **WHEN** a user opens an issue whose description contains Markdown headings, lists, links, bare URLs, emphasis, strikethrough, inline code, fenced code blocks, blockquotes, or horizontal rules
- **THEN** the Description section renders those structures as formatted content
- **AND** raw Markdown markers such as heading prefixes, list prefixes, and code fences are not shown as the primary reading experience

#### Scenario: Comment Markdown is readable

- **WHEN** a user opens an issue with comments containing Markdown formatting or code snippets
- **THEN** each comment body renders the Markdown as formatted content
- **AND** comment timestamps and delete actions remain available

### Requirement: REQ-WUI-ISSUE-MARKDOWN-002 Issue Detail provides readable Markdown code styling

Issue Detail Page Markdown rendering SHALL visually distinguish inline code and fenced code blocks while preserving the existing compact gray page styling. Inline code SHALL use a light gray background, monospaced font, rounded corners, and compact padding; fenced code blocks SHALL use a light gray background, monospaced font, rounded corners, padding, and horizontal scrolling for long lines.

#### Scenario: Inline code is visually distinct

- **WHEN** a description or comment contains inline Markdown code
- **THEN** the rendered inline code is visually distinct from surrounding prose
- **AND** it uses compact styling consistent with the page text size

#### Scenario: Code block is readable

- **WHEN** a description or comment contains a fenced code block
- **THEN** the code block renders with a distinct background and monospaced font
- **AND** long lines can be read by horizontal scrolling without breaking the page layout

### Requirement: REQ-WUI-ISSUE-MARKDOWN-003 Issue Detail collapses long descriptions

Issue Detail Page SHALL keep long descriptions from dominating the first screen by collapsing descriptions that exceed the readability threshold around 600px. The user SHALL be able to expand the description to read the full rendered Markdown and collapse it again.

#### Scenario: Long description is collapsed by default

- **WHEN** a user opens an issue with a description longer than the collapse threshold
- **THEN** the Description section initially constrains the rendered content height to about 600px
- **AND** an expand control is available

#### Scenario: User expands and collapses description

- **WHEN** the user activates the expand control on a collapsed description
- **THEN** the full rendered Markdown description becomes visible
- **AND** a collapse control is available to restore the constrained view

#### Scenario: Existing issue actions still work

- **WHEN** a user edits the issue, submits a comment, or deletes a comment after Markdown rendering is enabled
- **THEN** the existing action still uses the existing API flow
- **AND** the issue detail data refresh behavior remains unchanged
