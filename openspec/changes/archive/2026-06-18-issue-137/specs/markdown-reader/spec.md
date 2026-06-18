## MODIFIED Requirements

### Requirement: REQ-MDR-010 Issue description and comments render through MarkdownReader

The issue description and issue comment Markdown surfaces SHALL render through `MarkdownReader` instead of a page-local `react-markdown` wrapper. The issue description SHALL use a base heading level greater than 1 so embedded Markdown headings do not create duplicate page-level `h1` landmarks. On the issue description and issue comment surfaces, `MarkdownReader` SHALL resolve attachment references of the form `![name](att:id)` (image) and `[name](att:id)` (non-image): an image-typed attachment SHALL render inline at its markdown position and SHALL open into a full-screen lightbox on click; a non-image attachment SHALL render as a downloadable file card showing the filename, size, and type.

#### Scenario: Issue description renders via MarkdownReader

- **WHEN** a user opens an issue whose description contains Markdown
- **THEN** the description is rendered through `MarkdownReader`
- **AND** no page-local `MarkdownContent` wrapper or page-local collapse state owns the rendered output

#### Scenario: Issue comments render via MarkdownReader

- **WHEN** a user opens an issue with Markdown comments
- **THEN** each comment body renders through `MarkdownReader`
- **AND** comment timestamps and delete actions remain available

#### Scenario: Attachment image reference renders inline on issue and comment surfaces

- **WHEN** `MarkdownReader` renders an issue description or comment containing an image attachment reference `![name](att:id)`
- **THEN** the referenced image SHALL render inline at its markdown position
- **AND** it SHALL not render as a plain broken link

#### Scenario: Inline attachment image opens a full-screen lightbox

- **WHEN** a user clicks an inline attachment image rendered on an issue description or comment
- **THEN** the image SHALL open into a full-screen lightbox
- **AND** dismissing the lightbox SHALL return the user to the document

#### Scenario: Non-image attachment reference renders as a downloadable file card

- **WHEN** `MarkdownReader` renders an issue description or comment containing a non-image attachment reference `[name](att:id)`
- **THEN** the reference SHALL render as a downloadable file card showing the filename, size, and type
- **AND** it SHALL not render as a plain text link

#### Scenario: Unresolved attachment reference renders a safe fallback

- **WHEN** `MarkdownReader` renders an issue description or comment containing an `att:id` reference whose attachment cannot be resolved
- **THEN** the reader SHALL render a safe fallback in place of the reference
- **AND** it SHALL NOT fetch or execute untrusted content
