## ADDED Requirements

### Requirement: CLI issue create parses YAML frontmatter from body file

`mo issue create --body-file <file>` SHALL parse YAML frontmatter from the body file when the file begins with `---`. Recognized frontmatter fields SHALL be extracted and used to auto-populate the issue's `workflowProfileId` and `risk` fields.

#### Scenario: Body file with recommended_workflow auto-fills workflow profile

- **WHEN** the user runs `mo issue create "Title" --body-file body.md`
- **AND** `body.md` begins with YAML frontmatter containing `recommended_workflow: feature-flow`
- **THEN** the CLI SHALL parse the `recommended_workflow` field
- **AND** send `workflowProfileId: "feature-flow"` in the create request body
- **AND** the created issue SHALL have the `feature-flow` workflow profile assigned

#### Scenario: Body file with risk auto-fills risk

- **WHEN** the user runs `mo issue create "Title" --body-file body.md`
- **AND** `body.md` frontmatter contains `risk: high`
- **THEN** the CLI SHALL parse the `risk` field
- **AND** send `risk: "high"` in the create request body
- **AND** the created issue SHALL have risk set to `high`

#### Scenario: Body file frontmatter with both workflow and risk

- **WHEN** the user runs `mo issue create "Title" --body-file body.md`
- **AND** frontmatter contains both `recommended_workflow` and `risk`
- **THEN** the CLI SHALL parse and send both values in the create request

#### Scenario: Body file without frontmatter emits warning

- **WHEN** the user runs `mo issue create "Title" --body-file body.md`
- **AND** `body.md` does not begin with `---` (no frontmatter)
- **THEN** the CLI SHALL emit a warning: "No frontmatter found in body file. Consider including recommended_workflow and risk."
- **AND** the issue SHALL still be created successfully

#### Scenario: Malformed YAML frontmatter emits warning

- **WHEN** the user runs `mo issue create "Title" --body-file body.md`
- **AND** `body.md` begins with `---` but contains invalid YAML
- **THEN** the CLI SHALL emit a warning about the parse failure
- **AND** the issue SHALL still be created successfully with the full body text

#### Scenario: Body file with unrecognized frontmatter fields

- **WHEN** frontmatter contains fields other than `recommended_workflow`, `recommended_workflow_reason`, or `risk`
- **THEN** the CLI SHALL silently ignore unrecognized fields
- **AND** the issue SHALL still be created with recognized fields applied

### Requirement: CLI flags override frontmatter values

Explicit CLI flags SHALL take precedence over values parsed from body file frontmatter.

#### Scenario: --workflow-profile flag overrides frontmatter

- **WHEN** the user runs `mo issue create "Title" --body-file body.md --workflow-profile mohist/default`
- **AND** `body.md` frontmatter contains `recommended_workflow: feature-flow`
- **THEN** the CLI SHALL use `mohist/default` from the explicit flag
- **AND** the CLI SHALL emit a note indicating the flag overrode the frontmatter value

#### Scenario: Explicit risk flag overrides frontmatter (if risk flag exists)

- **WHEN** an explicit risk-related flag is provided alongside a body file with frontmatter risk
- **THEN** the explicit flag SHALL take precedence

### Requirement: CLI issue create emits frontmatter-aware success output

Successful `mo issue create` output SHALL include workflow and risk information when present, and SHALL update the start tip to include the workflow context.

#### Scenario: Success output includes workflow and risk

- **WHEN** `mo issue create` succeeds with a workflow profile and risk set (from frontmatter or flags)
- **THEN** the output SHALL include "Workflow: <profile>" and "Risk: <level>"

#### Scenario: Success output without workflow still works

- **WHEN** `mo issue create` succeeds without a workflow profile or risk
- **THEN** the output SHALL follow the existing format without adding workflow or risk lines
