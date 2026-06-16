## ADDED Requirements

### Requirement: Issue body frontmatter carries workflow recommendation and risk metadata

Issue body files written for `mo issue create --body-file` SHALL support an optional YAML frontmatter block delimited by `---` at the start of the file. The frontmatter SHALL carry advisory metadata that the CLI and Web UI can use to pre-fill issue fields. Frontmatter is advisory: its presence or absence SHALL NOT block issue creation.

The recognized frontmatter fields are:

- `recommended_workflow`: The workflow profile ID to recommend (e.g., `feature-flow`, `mohist/default`)
- `recommended_workflow_reason`: Human-readable explanation of why this workflow fits
- `risk`: Estimated risk level for the change (`low`, `medium`, `high`)

#### Scenario: Body file with complete frontmatter

- **WHEN** a body file begins with valid YAML frontmatter containing `recommended_workflow`, `recommended_workflow_reason`, and `risk`
- **THEN** the frontmatter block is recognized as the advisory metadata section
- **AND** the remainder of the file after the closing `---` is the issue body

#### Scenario: Body file without frontmatter

- **WHEN** a body file does not begin with `---`
- **THEN** the entire file content SHALL be treated as the issue body
- **AND** no frontmatter fields are parsed

#### Scenario: Body file with incomplete frontmatter

- **WHEN** a body file begins with `---` and contains only some recognized fields (e.g., `recommended_workflow` but no `risk`)
- **THEN** the recognized fields SHALL be parsed and applied
- **AND** missing fields SHALL be treated as absent (not present)

#### Scenario: Malformed frontmatter

- **WHEN** a body file begins with `---` but contains invalid YAML between the delimiters
- **THEN** the frontmatter parsing SHALL fail gracefully
- **AND** the entire file SHALL be treated as the issue body
- **AND** a warning SHALL be emitted about the malformed frontmatter

### Requirement: Frontmatter is advisory, not blocking

Missing or invalid frontmatter SHALL NOT prevent issue creation. The frontmatter SHALL be treated as a recommendation layer that can be overridden or absent.

#### Scenario: Issue creation succeeds without frontmatter

- **WHEN** `mo issue create` is invoked with `--body-file` pointing to a file with no frontmatter
- **THEN** the issue SHALL be created successfully
- **AND** a warning SHALL be emitted indicating that no frontmatter was found

#### Scenario: CLI flags override frontmatter

- **WHEN** `mo issue create` is invoked with both `--body-file` (containing `recommended_workflow: feature-flow`) and `--workflow-profile mohist/default`
- **THEN** the explicit CLI flag `--workflow-profile` SHALL take precedence over the frontmatter value
