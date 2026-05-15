## MODIFIED Requirements

### Requirement: Check full verification policy compatibility

Workflow configuration SHALL resolve the Check full verification command from `healthGates.check` when present and from legacy `checks.buildTest` when `healthGates.check` is absent.

#### Scenario: healthGates.check configures Check verification

- **WHEN** workflow configuration defines `healthGates.check.command` or related policy fields
- **THEN** Check full verification SHALL use the resolved `healthGates.check` policy

#### Scenario: checks.buildTest configures Check verification by compatibility

- **WHEN** workflow configuration defines `checks.buildTest`
- **AND** `healthGates.check` is absent
- **THEN** Check full verification SHALL use the compatible `checks.buildTest` command, timeout, and retry policy fields

#### Scenario: Disabled Check verification cannot satisfy approval evidence

- **WHEN** `healthGates.check.enabled` is `false`
- **THEN** the system SHALL record that Check verification was disabled by policy
- **AND** disabled verification SHALL NOT count as passing full verification evidence for Check approval
