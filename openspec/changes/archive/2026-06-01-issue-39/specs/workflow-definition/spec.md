## ADDED Requirements

### Requirement: Issue workflow profiles are normalized snapshot definitions
The workflow definition system SHALL support issue-scoped workflow profile snapshots derived from submitted YAML. The submitted YAML SHALL be normalized into a canonical `WorkflowDefinition` snapshot before persistence and later stage initialization.

#### Scenario: Save stores normalized workflow definition snapshot
- **WHEN** issue workflow profile YAML is accepted for saving
- **THEN** the system normalizes the submitted workflow definition into its canonical persisted form
- **AND** future reads for that issue return YAML generated from the normalized snapshot

#### Scenario: Issue snapshot is independent from later global edits
- **WHEN** an issue already has a persisted workflow profile snapshot
- **AND** the project or global workflow profile changes later
- **THEN** the issue continues to use its own persisted normalized workflow definition snapshot unless that issue is explicitly edited
