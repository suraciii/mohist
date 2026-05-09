## MODIFIED Requirements

### Requirement: integrate final health gate
The system SHALL run final integration health verification during Integrate after merge and before Done.

#### Scenario: Existing postMerge config is honored
- **WHEN** workflow configuration defines `healthGates.postMerge`
- **THEN** Integrate uses that policy for final integration health verification
- **AND** reports the result as Integrate final health evidence

#### Scenario: Final health failure blocks Done
- **WHEN** the final integration health command fails or times out
- **THEN** Integrate records command, duration, summary, and log excerpt
- **AND** the issue does not enter Done
