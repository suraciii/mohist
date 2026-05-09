## MODIFIED Requirements

### Requirement: integrate stage progression
Pipeline Mode SHALL include an explicit Integrate stage between Check and Done.

#### Scenario: Check approval enters Integrate
- **WHEN** Check passes and the user approves the candidate
- **THEN** the issue stage becomes `integrate`
- **AND** the issue is not displayed or reported as Done

#### Scenario: Integrate success enters Done
- **WHEN** spec sync, OpenSpec change archive, merge, and final integration health verification all succeed
- **THEN** the issue stage becomes `done`
- **AND** the issue status becomes completed

#### Scenario: Integrate failure blocks Done
- **WHEN** any Integrate step fails
- **THEN** the issue remains visible at `integrate` or blocked/interrupted with Integrate context
- **AND** Done is not reached

### Requirement: done means integration completed
Done SHALL mean that the approved candidate has been integrated into the project's canonical state.

#### Scenario: Done evidence exists
- **WHEN** a user opens a Done issue
- **THEN** integration evidence is available for spec sync, archive path, merge truth, and final health result
- **AND** Done does not mean merely approved or merge queued
