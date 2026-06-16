## ADDED Requirements

### Requirement: Explore skill produces frontmatter-annotated issue body

When the explore skill concludes an exploration session and produces an issue body, the output SHALL include YAML frontmatter with `recommended_workflow`, `recommended_workflow_reason`, and `risk` fields, followed by structured sections. The body SHALL be written to a file suitable for `mo issue create --body-file`.

#### Scenario: Explore produces complete issue body file

- **WHEN** the explore skill finishes an exploration and the user confirms issue creation
- **THEN** the skill writes a body file containing:
  - YAML frontmatter with `recommended_workflow`, `recommended_workflow_reason`, and `risk`
  - `## Background` section
  - `## Goal` section
  - `## Non-goals` section
  - `## Acceptance criteria` section

#### Scenario: Explore body file is pipeable to issue creation

- **WHEN** the explore skill produces a body file
- **THEN** the file content SHALL be valid input for `mo issue create <title> --body-file <file>`
- **AND** the CLI SHALL parse the frontmatter and the structured body sections

### Requirement: Explore skill discovers available workflows

Before recommending a workflow, the explore skill SHALL call `mo workflow list --described` to discover available workflow profiles and their suitability metadata.

#### Scenario: Skill queries workflow list

- **WHEN** the explore skill is about to recommend a workflow
- **THEN** it SHALL execute `mo workflow list --described`
- **AND** parse the output to extract each workflow's profile ID, description, and `suitable_for` metadata

#### Scenario: Skill selects best-fit workflow

- **WHEN** the explore skill has parsed the available workflow list
- **THEN** it SHALL match the exploration context against `suitable_for` descriptions
- **AND** populate `recommended_workflow` with the best-matching profile ID
- **AND** populate `recommended_workflow_reason` with the matching `suitable_for` description or a derived explanation

#### Scenario: No workflow matches exploration context

- **WHEN** no workflow's `suitable_for` description matches the exploration findings
- **THEN** the skill SHALL default to `mohist/default` as the recommended workflow
- **AND** the `recommended_workflow_reason` SHALL explain that no specific workflow matched

### Requirement: Explore skill assesses risk

The explore skill SHALL estimate issue risk based on the scope and nature of findings and populate the `risk` frontmatter field.

#### Scenario: Risk is populated

- **WHEN** the explore skill produces a body file
- **THEN** the `risk` field SHALL be set to one of `low`, `medium`, or `high`
- **AND** the assessment SHALL be based on change scope, affected systems, and complexity

### Requirement: User confirms before issue creation

The explore skill SHALL present the produced frontmatter and structured body to the user for confirmation before invoking `mo issue create`.

#### Scenario: User confirms issue creation from explore output

- **WHEN** the explore skill has produced a body file
- **THEN** the skill SHALL present the recommended workflow, risk, and body summary to the user
- **AND** wait for user confirmation before running `mo issue create --body-file <file>`

#### Scenario: User rejects or modifies recommendation

- **WHEN** the user wants to change the recommended workflow or risk before creation
- **THEN** the skill SHALL allow the user to modify the frontmatter values
- **AND** the issue SHALL be created with the user-modified values
