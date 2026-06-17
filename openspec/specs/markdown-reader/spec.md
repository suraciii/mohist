# OpenSpec Capability: markdown-reader

### Requirement: REQ-MDR-001 Shared MarkdownReader component exists in shared UI

The web app SHALL provide a reusable `MarkdownReader` component under shared UI that renders Markdown content on top of the existing `react-markdown` + `remark-gfm` + Tailwind typography stack. The component SHALL accept a `MarkdownReaderProps` contract containing at minimum `content: string`, `baseHeadingLevel?: 1 | 2 | 3 | 4 | 5 | 6`, `mode?: 'full' | 'collapsible'`, `collapsedHeight?: number`, `showToc?: boolean`, `showHeadingAnchors?: boolean`, `showCopyCode?: boolean`, and `className?: string`. Consumers SHALL be able to render both trusted and agent-generated Markdown through this single component instead of calling `react-markdown` directly.

#### Scenario: Component is importable from shared UI

- **WHEN** a consumer imports `MarkdownReader` from the shared UI module
- **THEN** the component is available with the `MarkdownReaderProps` signature
- **AND** rendering it with Markdown `content` produces formatted document output rather than raw Markdown text

#### Scenario: Default rendering behaves as a Markdown document

- **WHEN** `MarkdownReader` is rendered with `content` containing paragraphs, headings, lists, links, emphasis, inline code, fenced code blocks, blockquotes, and horizontal rules
- **THEN** all of those Markdown structures are rendered as formatted document content
- **AND** raw Markdown markers such as heading prefixes, list prefixes, and code fences are not the primary reading experience

### Requirement: REQ-MDR-002 Consumers can set a base heading level

`MarkdownReader` SHALL remap rendered Markdown `h1` through `h6` headings by applying a `baseHeadingLevel` offset through `react-markdown` component overrides. A Markdown `#` heading rendered with `baseHeadingLevel={2}` SHALL produce an `h2` element, a `##` heading SHALL produce an `h3` element, and clamped heading levels SHALL stay within the valid `h1`-`h6` range.

#### Scenario: Headings are shifted by the base level

- **WHEN** `MarkdownReader` is rendered with `baseHeadingLevel={3}` and content containing `# Title`, `## Section`, and `### Subsection`
- **THEN** the rendered heading elements are `h3`, `h4`, and `h5` respectively
- **AND** no `h1` element is rendered for those embedded headings

#### Scenario: Heading levels clamp within h1-h6

- **WHEN** `baseHeadingLevel` plus the original heading level exceeds 6
- **THEN** the rendered heading element SHALL clamp at `h6`
- **AND** it SHALL NOT produce an invalid heading tag

### Requirement: REQ-MDR-003 MarkdownReader never emits a page-level h1 from embedded content

`MarkdownReader` SHALL NOT render an embedded Markdown `#` heading as a page-level `h1` landmark when a consumer sets a `baseHeadingLevel` greater than 1. A single rendered `MarkdownReader` SHALL emit at most one heading level at or above the configured base level per embedded heading, so that document-level Markdown cannot create duplicate page-level `h1` landmarks that compete with the surrounding page title.

#### Scenario: Embedded h1 is demoted

- **WHEN** `MarkdownReader` is rendered with `baseHeadingLevel={2}` and content whose first line is `# Embedded Title`
- **THEN** the rendered document does not contain an `h1` for `Embedded Title`
- **AND** the surrounding page title remains the only page-level `h1` landmark

### Requirement: REQ-MDR-004 Code blocks do not cause page-level horizontal scrolling

`MarkdownReader` SHALL render fenced code blocks with stable overflow handling so that long code lines do not expand the page width on desktop or mobile. Long code lines SHALL be readable through horizontal scrolling contained inside the code block, not through page-level horizontal scrollbars.

#### Scenario: Long code line stays inside the code block

- **WHEN** `MarkdownReader` renders a fenced code block containing a single very long line
- **THEN** the code block becomes horizontally scrollable within its own container
- **AND** the surrounding page layout does not gain a horizontal scrollbar

### Requirement: REQ-MDR-005 Optional copy-code action is available on code blocks

When `showCopyCode` is enabled, `MarkdownReader` SHALL render a copy affordance on fenced code blocks that copies the block's raw text to the clipboard. When `showCopyCode` is disabled or omitted, no copy affordance SHALL be rendered.

#### Scenario: Copy-code button appears when enabled

- **WHEN** `MarkdownReader` is rendered with `showCopyCode` enabled and content containing a fenced code block
- **THEN** a copy affordance is rendered for that code block
- **AND** activating it copies the code block's raw text to the clipboard

#### Scenario: Copy-code button hidden when disabled

- **WHEN** `MarkdownReader` is rendered without `showCopyCode`
- **THEN** no copy affordance is rendered on fenced code blocks

### Requirement: REQ-MDR-006 Tables are contained and readable inside constrained containers

`MarkdownReader` SHALL wrap rendered Markdown tables in a horizontal scroll container. Tables SHALL remain readable inside constrained desktop and mobile containers and SHALL NOT expand the surrounding page width.

#### Scenario: Wide table scrolls inside its container

- **WHEN** `MarkdownReader` renders a Markdown table with many columns or long cell content
- **THEN** the table scrolls horizontally within its wrapper element
- **AND** the page layout does not gain a horizontal scrollbar and the page width does not grow to fit the table

### Requirement: REQ-MDR-007 Long links, paths, inline code, and identifiers wrap or scroll without breaking layout

`MarkdownReader` SHALL apply break/wrap behavior (such as `overflow-wrap: anywhere`) to links, inline code, long filesystem paths, and generated identifiers so that long unbroken strings do not force the surrounding layout to grow wider than its container on desktop or mobile.

#### Scenario: Long URL wraps inside a paragraph

- **WHEN** `MarkdownReader` renders a paragraph containing a very long bare URL or link
- **THEN** the URL wraps or breaks within the paragraph container
- **AND** the page layout does not grow wider than its container

#### Scenario: Long filesystem path wraps inside inline code

- **WHEN** `MarkdownReader` renders inline code containing a very long filesystem path or generated identifier
- **THEN** the inline code wraps or breaks within its container
- **AND** the surrounding line does not overflow horizontally

### Requirement: REQ-MDR-008 MarkdownReader supports full and collapsible long-document modes

`MarkdownReader` SHALL support a `mode` prop with values `full` and `collapsible`. In `full` mode the entire rendered document SHALL be visible with no Reader-level collapse control. In `collapsible` mode the Reader SHALL initially constrain the rendered content height to `collapsedHeight` (defaulting to a readability threshold around 600px when omitted), expose an expand control, allow the user to expand to the full document and collapse again, and own this behavior at the Reader level rather than at the consuming page.

#### Scenario: Collapsible mode collapses long content by default

- **WHEN** `MarkdownReader` is rendered with `mode="collapsible"` and content taller than the collapse threshold
- **THEN** the rendered content is initially constrained to the configured `collapsedHeight` (or ~600px when omitted)
- **AND** an expand control is available

#### Scenario: Collapsible mode expands and collapses

- **WHEN** the user activates the expand control on a collapsed `MarkdownReader`
- **THEN** the full rendered content becomes visible
- **AND** a collapse control is available to restore the constrained view

#### Scenario: Full mode never collapses

- **WHEN** `MarkdownReader` is rendered with `mode="full"`
- **THEN** the entire rendered document is visible without a Reader-level collapse control

#### Scenario: Short content in collapsible mode does not collapse

- **WHEN** `MarkdownReader` is rendered with `mode="collapsible"` and content shorter than the collapse threshold
- **THEN** no expand control is rendered
- **AND** the full content is visible

### Requirement: REQ-MDR-009 Optional table of contents and heading anchors are reader-controlled

When `showToc` is enabled, `MarkdownReader` SHALL render a compact table of contents derived from the document's headings. When `showHeadingAnchors` is enabled, `MarkdownReader` SHALL render anchor affordances on headings. When either option is disabled or omitted, the corresponding affordance SHALL NOT be rendered.

#### Scenario: Table of contents appears when enabled

- **WHEN** `MarkdownReader` is rendered with `showToc` enabled and content containing multiple headings
- **THEN** a compact table of contents listing those headings is rendered

#### Scenario: Heading anchors appear when enabled

- **WHEN** `MarkdownReader` is rendered with `showHeadingAnchors` enabled
- **THEN** headings expose anchor affordances

#### Scenario: Optional affordances are absent by default

- **WHEN** `MarkdownReader` is rendered without `showToc` and without `showHeadingAnchors`
- **THEN** neither a table of contents nor heading anchors are rendered

### Requirement: REQ-MDR-010 Issue description and comments render through MarkdownReader

The issue description and issue comment Markdown surfaces SHALL render through `MarkdownReader` instead of a page-local `react-markdown` wrapper. The issue description SHALL use a base heading level greater than 1 so embedded Markdown headings do not create duplicate page-level `h1` landmarks.

#### Scenario: Issue description renders via MarkdownReader

- **WHEN** a user opens an issue whose description contains Markdown
- **THEN** the description is rendered through `MarkdownReader`
- **AND** no page-local `MarkdownContent` wrapper or page-local collapse state owns the rendered output

#### Scenario: Issue comments render via MarkdownReader

- **WHEN** a user opens an issue with Markdown comments
- **THEN** each comment body renders through `MarkdownReader`
- **AND** comment timestamps and delete actions remain available

### Requirement: REQ-MDR-011 Artifact Markdown renders through MarkdownReader

Artifact Markdown content SHALL render through `MarkdownReader` instead of a plain preformatted text (`<pre>`) block. Artifact readers SHALL be able to set a base heading level so embedded Markdown headings do not become page-level `h1` landmarks.

#### Scenario: Artifact Markdown uses MarkdownReader

- **WHEN** a user opens an artifact whose recorded content is Markdown
- **THEN** the artifact content renders through `MarkdownReader`
- **AND** the artifact Markdown no longer renders inside a plain preformatted text (`<pre>`) block

### Requirement: REQ-MDR-012 Component tests cover MarkdownReader behavior

`MarkdownReader` SHALL have component tests covering heading-level remapping, code-block overflow, table containment, long link/path wrapping, and `full` vs `collapsible` reader modes. Issue and artifact Markdown tests SHALL be updated to assert the new Reader behavior instead of page-local Markdown behavior.

#### Scenario: Heading remapping is covered

- **WHEN** the `MarkdownReader` component tests run
- **THEN** they assert that `baseHeadingLevel` shifts rendered heading elements as expected

#### Scenario: Code, table, and link overflow are covered

- **WHEN** the `MarkdownReader` component tests run
- **THEN** they assert that long code lines, wide tables, long URLs, and long inline code paths do not produce page-level horizontal overflow

#### Scenario: Reader modes are covered

- **WHEN** the `MarkdownReader` component tests run
- **THEN** they assert that `collapsible` mode collapses, expands, and restores and that `full` mode never collapses

#### Scenario: Issue and artifact Markdown tests assert Reader behavior

- **WHEN** the issue detail and artifact Markdown tests run
- **THEN** they assert that those surfaces render through `MarkdownReader` and no longer assert page-local `scrollHeight`-based collapse behavior
