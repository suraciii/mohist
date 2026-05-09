## MODIFIED Requirements

### Requirement: check integration readiness
The Check stage SHALL verify that a candidate is ready to integrate before requesting user approval.

#### Scenario: Readiness checks pass before approval
- **WHEN** Check runs for an OpenSpec change
- **THEN** build/test validation, AI review, spec sync dry-run, and mergeability checks pass before approval is requested
- **AND** the approval output includes integration readiness information

#### Scenario: Readiness failure blocks approval
- **WHEN** spec sync dry-run or mergeability fails
- **THEN** Check fails or escalates for repair
- **AND** user approval is not requested

### Requirement: Check does not integrate
The Check stage SHALL remain read-only with respect to canonical integration artifacts.

#### Scenario: Check completes successfully
- **WHEN** Check succeeds and awaits or receives user approval
- **THEN** it does not write `openspec/specs/`
- **AND** it does not archive the OpenSpec change
- **AND** it does not merge the candidate
- **AND** it does not mark the issue Done
