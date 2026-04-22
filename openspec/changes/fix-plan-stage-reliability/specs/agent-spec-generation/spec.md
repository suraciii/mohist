## MODIFIED Requirements

### Requirement: Spec completeness validation
The system SHALL validate that generated specs cover the full scope of the issue. All plan stage artifacts (proposal.md, specs/, design.md, tasks.json) are mandatory — the agent SHALL NOT skip any artifact regardless of perceived complexity. Prompt templates SHALL use mandatory language and SHALL NOT contain skip permissions.

#### Scenario: Validate all artifacts generated
- **WHEN** the plan stage completes a round for an artifact type
- **THEN** the system verifies the corresponding file exists in the change directory
- **AND** if the file does not exist, the plan stage fails with a descriptive error

#### Scenario: Design prompt does not allow skipping
- **WHEN** the agent receives the design prompt
- **THEN** the prompt instructs the agent to generate design.md unconditionally
- **AND** the prompt does not contain language like "you may skip this file"
- **AND** for simple changes the agent generates a minimal but valid design.md

#### Scenario: Self-review treats all artifacts as expected
- **WHEN** the agent performs self-review
- **THEN** the review prompt lists design.md as a required artifact (not "if it exists")
- **AND** missing design.md is flagged as a review failure
