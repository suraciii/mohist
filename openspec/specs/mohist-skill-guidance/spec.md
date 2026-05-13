# OpenSpec Capability: mohist-skill-guidance

### Requirement: Mohist skill guidance recommends file-backed long issue bodies

The shared `mohist` skill guidance SHALL recommend file-backed or piped issue body workflows for long Markdown content instead of relying on shell-escaped inline strings as the primary path.

#### Scenario: Recommend file-backed issue body workflow
- **WHEN** the skill documents how to create an issue with a long Markdown body
- **THEN** it recommends `mo issue create "Title" --body @file.md` as the default workflow

#### Scenario: Recommend stdin workflow
- **WHEN** the skill documents pipeline-friendly issue creation
- **THEN** it documents `mo issue create "Title" --body -` as the stdin workflow

#### Scenario: Keep heredoc as fallback guidance
- **WHEN** the skill documents compatibility alternatives
- **THEN** it may mention heredoc or command substitution as fallback patterns
- **AND** it does not present shell-escaping long inline strings as the preferred workflow

