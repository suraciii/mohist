## ADDED Requirements

### Requirement: Issue-scoped workflow profile snapshot editing
The system SHALL expose an issue-scoped workflow profile YAML snapshot for each issue, separate from the project or global default workflow profile. Editing this YAML SHALL only mutate the targeted issue workflow profile snapshot.

#### Scenario: View issue workflow profile snapshot
- **WHEN** a client requests the workflow profile YAML for a specific issue
- **THEN** the system returns that issue's normalized workflow profile YAML snapshot
- **AND** the response does not depend on re-reading the current global default workflow YAML

#### Scenario: Saving issue workflow profile does not mutate global defaults
- **WHEN** a user saves edited workflow profile YAML for one issue
- **THEN** the persisted workflow profile snapshot for that issue is updated
- **AND** the project or global default workflow profile remains unchanged
