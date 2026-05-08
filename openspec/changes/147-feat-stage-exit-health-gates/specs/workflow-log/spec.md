## MODIFIED Requirements

### Requirement: health gate check result details

Stage execution check results SHALL represent health gates as first-class check results with enough metadata for users to diagnose failures without reading raw logs.

#### Scenario: Passing health gate result
- **WHEN** a health gate command passes
- **THEN** the stage execution check result SHALL include the health gate name, command, duration, enabled status, and a bounded log excerpt

#### Scenario: Failing health gate result
- **WHEN** a health gate command fails or times out
- **THEN** the stage execution check result SHALL include the health gate name, command, duration, enabled status, exit code or timeout marker, concise error summary, and bounded log excerpt

#### Scenario: Disabled health gate result
- **WHEN** a health gate is disabled by configuration
- **THEN** the stage execution check result SHALL indicate the gate was disabled by policy
- **AND** no command output SHALL be required
