### Requirement: Recognized issue frontmatter is presented as metadata

When an issue body begins with well-formed frontmatter containing `recommended_workflow`, `recommended_workflow_reason`, or `risk`, the issue detail page SHALL interpret those values as structured issue metadata rather than description content. Present values SHALL be available in the issue's Details area with labels that communicate their meaning.

#### Scenario: Issue body contains all recognized frontmatter values

- **WHEN** an issue body begins with well-formed frontmatter containing a recommended workflow, its reason, and risk
- **THEN** the Details area SHALL present the recommended workflow, recommendation reason, and risk as structured metadata
- **AND** those values SHALL NOT depend on rendering the frontmatter as Markdown

#### Scenario: Issue body contains only some recognized values

- **WHEN** an issue body begins with well-formed frontmatter containing only a subset of the recognized fields
- **THEN** the Details area SHALL present each recognized value that is present
- **AND** it SHALL NOT fabricate values for fields that are absent

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
