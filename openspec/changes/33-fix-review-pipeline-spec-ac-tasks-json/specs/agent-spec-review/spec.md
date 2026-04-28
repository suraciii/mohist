## MODIFIED Requirements

### Requirement: Automated spec consistency review
The system SHALL automatically review generated specs for consistency with proposal and design during the review stage. The review agent SHALL receive full spec context to enable spec-compliance checking of the implementation.

#### Scenario: Agent reviews spec consistency
- **WHEN** the review stage starts
- **THEN** the system spawns a review agent
- **AND** the agent reads proposal.md, design.md, and all specs/
- **AND** the agent checks:
  - Does proposal's intent match the specs coverage?
  - Does design's approach support all spec requirements?
  - Are there missing edge cases in specs?
- **AND** the agent outputs a review report

### Requirement: Review prompt includes spec and AC context
The `buildReviewerPrompt` function SHALL inject the change's specs/ directory content and tasks.json into the review prompt, enabling the review agent to verify spec compliance.

#### Scenario: Build reviewer prompt with spec context
- **WHEN** `buildReviewerPrompt` is called with a changeDir
- **THEN** the system SHALL read all spec files under `{changeDir}/specs/`
- **AND** the system SHALL read `{changeDir}/tasks.json`
- **AND** the prompt SHALL include a "## Specs" section with full spec content
- **AND** the prompt SHALL include a "## Tasks & Acceptance Criteria" section with tasks.json content
- **AND** the review agent uses this context to verify implementation matches specs

#### Scenario: Build reviewer prompt without spec files
- **WHEN** `buildReviewerPrompt` is called with a changeDir that has no specs/ directory
- **THEN** the prompt SHALL proceed without the specs section (graceful degradation)

### Requirement: Review includes Spec Compliance dimension
The review instruction (review.md) SHALL include a "Spec Compliance" review dimension that checks implementation against acceptance criteria with exact value verification.

#### Scenario: Review agent checks spec compliance
- **WHEN** the review agent reviews implementation
- **THEN** the agent SHALL check a "Spec Compliance" dimension
- **AND** for each acceptance criterion in tasks.json, the agent SHALL verify:
  - The criterion is satisfied by the implementation
  - Exact values (colors, strings, formats, constants) match what the spec requires
- **AND** the review report SHALL include a "### Spec Compliance: PASS / FAIL" section
- **AND** any unmet criterion SHALL be listed with the specific deviation

#### Scenario: Spec Compliance dimension in report format
- **WHEN** the review report is generated
- **THEN** the Dimensions section SHALL include Spec Compliance alongside Correctness, Complexity, Test Coverage, and Security
- **AND** the report format SHALL be updated to include the new dimension
