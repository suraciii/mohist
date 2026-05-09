## MODIFIED Requirements

### Requirement: integration evidence in stage execution
The system SHALL record Integrate step evidence in stage execution/check result data.

#### Scenario: Integrate step succeeds
- **WHEN** an Integrate step succeeds
- **THEN** the stage execution contains a result for that step with status, summary, and structured output

#### Scenario: Integrate step fails
- **WHEN** an Integrate step fails
- **THEN** the stage execution contains the failing step, failure summary, and actionable details such as capability, requirement header, conflicted files, command, or log excerpt when available
