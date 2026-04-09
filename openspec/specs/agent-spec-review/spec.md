## ADDED Requirements

### Requirement: Automated spec consistency review
The system SHALL automatically review generated specs for consistency with proposal and design during the review stage.

#### Scenario: Agent reviews spec consistency
- **WHEN** the review stage starts
- **THEN** the system spawns a review agent
- **AND** the agent reads proposal.md, design.md, and all specs/
- **AND** the agent checks:
  - Does proposal's intent match the specs coverage?
  - Does design's approach support all spec requirements?
  - Are there missing edge cases in specs?
- **AND** the agent outputs a review report

### Requirement: Automatic spec fixes
The system SHALL automatically fix identified issues when possible, or ask user for clarification.

#### Scenario: Fix spec issues automatically
- **WHEN** the review agent identifies inconsistencies
- **THEN** it attempts to automatically fix straightforward issues
- **AND** for ambiguous issues, it asks the user for clarification via ask_user tool
- **AND** the process loops until all critical issues are resolved

### Requirement: Generate prd.json from specs
The system SHALL generate prd.json with structured tasks based on the reviewed specs.

#### Scenario: Parse specs into tasks
- **WHEN** specs pass consistency review
- **THEN** the system parses each spec file
- **AND** extracts requirements and scenarios
- **AND** generates tasks in prd.json format with:
  - id, title, description
  - spec reference (e.g., "specs/capability/spec.md#REQ-001")
  - acceptanceCriteria derived from scenarios
  - priority based on order in spec

### Requirement: Human review gate
The system SHALL pause for human review after automated review and prd.json generation.

#### Scenario: Human reviews generated artifacts
- **WHEN** automated review completes and prd.json is generated
- **THEN** the system adds a comment summarizing the generated artifacts
- **AND** pauses waiting for human approval
- **AND** the human can:
  - Edit any file (proposal, design, specs)
  - Regenerate prd.json if specs changed
  - Approve to proceed to build stage
  - Reject and request changes
