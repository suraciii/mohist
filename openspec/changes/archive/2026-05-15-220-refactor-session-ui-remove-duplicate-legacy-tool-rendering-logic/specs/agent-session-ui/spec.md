## MODIFIED Requirements

### Requirement: Semantic tool parts use a registry-based display model

Transcript tool rendering SHALL use a registry-based display contract and shared transcript parsing helpers so known tool families define human-readable title, subtitle, badges, display type, and file-change parsing without duplicating semantic logic across legacy and dedicated session transcript components.

#### Scenario: Known tools render semantic content

- **WHEN** bash, read, grep, glob, webfetch, question, task, skill, apply_patch, edit, or write tools appear in the transcript
- **THEN** legacy and registry-based transcript surfaces render human-readable headers and type-specific content using the same shared label, argument badge, display type, and patch/file-change parsing rules

#### Scenario: Running tools are visually distinct

- **WHEN** a tool is still running
- **THEN** the transcript shows a distinct animated running state rather than a static pending marker

#### Scenario: Tool display rules have one source of truth

- **WHEN** a developer adds or changes a transcript tool display rule
- **THEN** the change is made in `transcript-tool-utils` or `tool-registry`
- **AND** the legacy `ToolCallCard` path does not require a second copy of parsing logic for labels, arguments, display type, or patch operations

### Requirement: Transcript display summaries stay accurate for grouped and truncated content

Transcript presentation helpers SHALL keep summaries consistent with the rendered content, including grouped context tools, truncated search results, and legacy tool cards that consume shared transcript parsing helpers.

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

#### Scenario: Legacy and registry paths share file-change parsing

- **WHEN** `apply_patch`, `edit`, or `write` tools render changed-file summaries in either the legacy session view or the registry-based transcript view
- **THEN** both paths use shared patch/edit parsing helpers
- **AND** the visible file count, operation, path, and additions/deletions semantics remain consistent
