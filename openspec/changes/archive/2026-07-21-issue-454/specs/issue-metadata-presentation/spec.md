### Requirement: Recognized issue frontmatter is presented without overriding Issue state

When an issue body begins with well-formed frontmatter containing `recommended_workflow`, `recommended_workflow_reason`, or `risk`, the issue detail page SHALL interpret those values as structured metadata rather than description content. `recommended_workflow` and `recommended_workflow_reason` SHALL be labeled as recommendation metadata, distinct from the Issue's selected workflow profile. The authoritative Issue `risk` and selected workflow profile SHALL take precedence over conflicting body values; body `risk` SHALL be used only when authoritative Issue risk is absent.

#### Scenario: Issue body contains all recognized frontmatter values

- **WHEN** an issue body begins with well-formed frontmatter containing a recommended workflow, its reason, and risk
- **THEN** the Details area SHALL present the workflow and reason as recommendation metadata
- **AND** it SHALL present the authoritative Issue risk when one is available, otherwise the body risk
- **AND** those values SHALL NOT depend on rendering the frontmatter as Markdown

#### Scenario: Issue body contains only some recognized values

- **WHEN** an issue body begins with well-formed frontmatter containing only a subset of the recognized fields
- **THEN** the Details area SHALL present each recognized value that is present
- **AND** it SHALL NOT fabricate values for fields that are absent

#### Scenario: Body defaults conflict with current Issue fields

- **WHEN** body frontmatter recommends one workflow or risk while the Issue has a different selected workflow profile or authoritative risk
- **THEN** the selected workflow profile and authoritative Issue risk SHALL remain the current-state values
- **AND** the body workflow SHALL be labeled only as a recommendation
- **AND** the conflicting body risk SHALL NOT replace or duplicate the authoritative Issue risk

### Requirement: Description rendering excludes frontmatter

The rendered issue description SHALL contain only the body content after the leading frontmatter block. The frontmatter delimiters, keys, and values SHALL NOT appear in the expanded Markdown description or be interpreted as description headings or prose.

#### Scenario: Description follows frontmatter

- **WHEN** an issue body contains recognized leading frontmatter followed by Markdown description content
- **THEN** the expanded description SHALL begin with the Markdown content after the closing frontmatter delimiter
- **AND** the rendered description SHALL NOT display the frontmatter block or any of its delimiters

#### Scenario: Body contains frontmatter but no description

- **WHEN** an issue body contains recognized leading frontmatter and no content after it
- **THEN** the page SHALL NOT render the frontmatter as an issue description
- **AND** the recognized values SHALL remain available as structured metadata

### Requirement: Collapsed description hints exclude frontmatter

Any collapsed preview or leading-text hint for the description SHALL be derived only from the description content after the leading frontmatter block. Internal frontmatter keys, values, and delimiters SHALL NOT appear in the preview.

#### Scenario: Long description has a collapsed hint

- **WHEN** a long issue body begins with frontmatter and the description renders a collapsed hint
- **THEN** the hint SHALL begin with text from the description content
- **AND** it SHALL NOT contain `recommended_workflow`, `recommended_workflow_reason`, `risk`, or frontmatter delimiters

### Requirement: Malformed leading frontmatter does not enter the reading flow

A body whose first line is a frontmatter delimiter but whose envelope is malformed or lacks a closing delimiter SHALL NOT expose that raw envelope in the rendered description, collapsed preview, or description editor. A bounded malformed envelope SHALL preserve post-envelope description content. An unclosed envelope SHALL be treated as metadata-only content until a description edit repairs the boundary.

#### Scenario: Bounded frontmatter is malformed

- **WHEN** a leading frontmatter envelope has a closing delimiter but its fields are malformed
- **THEN** the raw envelope SHALL NOT render as description or preview content
- **AND** content after the closing delimiter SHALL remain the description
- **AND** no malformed value SHALL be presented as structured metadata

#### Scenario: Frontmatter has no closing delimiter

- **WHEN** an issue body begins with a frontmatter delimiter and has no closing delimiter
- **THEN** the raw body SHALL NOT render as description or preview content
- **AND** the description editor SHALL start empty rather than expose the malformed envelope
- **AND** no structured metadata value SHALL be fabricated

### Requirement: Description editing does not expose raw frontmatter

The issue edit dialog SHALL NOT place the raw frontmatter block in the description editor. Recognized metadata values SHALL either be edited through dedicated structured controls or remain unchanged when the user edits and saves only the description.

#### Scenario: User opens an issue containing frontmatter for editing

- **WHEN** the edit dialog opens for an issue whose body begins with recognized frontmatter
- **THEN** the description editor SHALL contain only the description content after the frontmatter block
- **AND** the editor SHALL NOT expose frontmatter delimiters or raw metadata keys

#### Scenario: User saves a description-only edit

- **WHEN** recognized metadata is not exposed as editable controls and the user saves a change to the description
- **THEN** the existing recognized metadata values SHALL be preserved
- **AND** the saved description SHALL contain the user's edited description content without presenting raw frontmatter in the editor

#### Scenario: Metadata has dedicated edit controls

- **WHEN** the edit dialog exposes a recognized metadata value for editing
- **THEN** it SHALL use a dedicated field for that value
- **AND** changing that field SHALL NOT require the user to edit raw frontmatter syntax

#### Scenario: User saves a description for an unclosed envelope

- **WHEN** the body has an unclosed leading envelope and the user saves new description content
- **THEN** the saved body SHALL preserve the original envelope text
- **AND** it SHALL insert a closing delimiter before the new description
- **AND** reopening the issue SHALL render the new description without exposing the repaired envelope
