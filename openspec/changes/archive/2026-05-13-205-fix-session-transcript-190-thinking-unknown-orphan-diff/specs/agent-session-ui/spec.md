## MODIFIED Requirements

### Requirement: Session transcript preserves readable inline assistant flow

The dedicated session page SHALL present prompt, reasoning, assistant text, tools, and results as one readable Mohist-to-Coder flow instead of detached transcript fragments.

#### Scenario: Thinking remains inline with assistant output

- **WHEN** a turn contains alternating reasoning and assistant text chunks
- **THEN** the visible transcript preserves that interleaving instead of rendering all thinking as one detached block at the top
- **AND** refreshing after a live run does not materially change that visible order

### Requirement: File-changing transcript tools render diff-first semantic content

The session transcript UI SHALL render `apply_patch`, `edit`, and `write` tools as diff-first semantic content rather than raw JSON-first payloads.

#### Scenario: Edit-like tools show file changes first

- **WHEN** a file-changing tool produces normalized changed-file metadata and diff content
- **THEN** the primary transcript body shows changed files and readable diff content
- **AND** raw input and output remain available through secondary disclosure for audit/debugging

### Requirement: Transcript display summaries stay accurate for grouped and truncated content

Transcript presentation helpers SHALL keep summaries consistent with the rendered content, including grouped context tools and truncated search results.

#### Scenario: Search ellipsis appears only when results were truncated

- **WHEN** a search content block renders all available results without truncation
- **THEN** no trailing ellipsis is shown
- **AND** an ellipsis is shown only when additional undisplayed results exist

#### Scenario: Grouped context tools still contribute changed-file summaries

- **WHEN** file-changing tools are nested inside a grouped context section
- **THEN** the turn-level changed-files summary includes those files
- **AND** a single context tool is rendered directly instead of being wrapped in a one-item group

#### Scenario: Shared transcript subtitle helpers are reused consistently

- **WHEN** a transcript tool needs a fallback subtitle
- **THEN** the transcript UI uses the shared subtitle helper instead of duplicating extraction logic
