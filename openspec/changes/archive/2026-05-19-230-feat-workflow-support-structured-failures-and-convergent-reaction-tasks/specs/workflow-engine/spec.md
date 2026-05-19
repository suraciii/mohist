## MODIFIED Requirements

### Requirement: REQ-WE-001 workflow engine converges through structured reactions

The workflow engine SHALL use structured failed context and verification-mode rechecks to converge after check failures while preserving task/check boundaries.

#### Scenario: Check failure schedules a reaction task

- **WHEN** a read-only check fails because parsed structured output contains blocking current-change items
- **THEN** the engine SHALL schedule the configured reaction task according to workflow policy
- **AND** the reaction task SHALL receive the full relevant blocking item batch
- **AND** the check itself SHALL NOT start agents, modify artifacts, or repair files

#### Scenario: Verification mode evaluates known items first

- **WHEN** a reaction task has attempted repairs
- **THEN** the engine SHALL re-run the configured task/check path in verification mode with known item IDs and expected repairs
- **AND** unresolved known blockers or policy-allowed new blockers SHALL keep the stage blocked with structured evidence
- **AND** a reaction task SHALL NOT directly mutate a failed check into pass without recheck evidence

#### Scenario: Existing review history remains compatible

- **WHEN** structured review convergence is enabled
- **THEN** existing review history behavior and reviewed-snapshot binding SHALL remain compatible and SHALL NOT be replaced by review-specific core domain state
