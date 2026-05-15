## MODIFIED Requirements

### Requirement: Check verification diagnostics

Stage check evidence and diagnostics SHALL expose Check full verification details needed to understand approval blocking without reading internal logs.

#### Scenario: Passing Check verification diagnostics

- **WHEN** Check full verification passes
- **THEN** persisted check evidence SHALL include the command, status, duration, and candidate metadata

#### Scenario: Failing Check verification diagnostics

- **WHEN** Check full verification fails or times out
- **THEN** persisted check evidence SHALL include the command, status, duration, concise summary, and useful bounded log excerpt
- **AND** the failure SHALL be visible as an approval-blocking Check-stage check result
