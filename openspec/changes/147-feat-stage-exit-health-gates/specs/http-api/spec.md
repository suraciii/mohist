## MODIFIED Requirements

### Requirement: direct merge respects final health gates

The direct merge API SHALL respect enabled final health gates and SHALL NOT bypass post-merge verification before marking an issue done/completed.

#### Scenario: Direct merge succeeds after final verification
- **WHEN** `POST /api/issues/:number/merge` merges an issue successfully
- **AND** the enabled post-merge health gate passes
- **THEN** the API SHALL return success
- **AND** the issue SHALL be marked merged and done/completed

#### Scenario: Direct merge reports final verification failure
- **WHEN** `POST /api/issues/:number/merge` merges an issue successfully
- **AND** the enabled post-merge health gate fails
- **THEN** the API SHALL return a failure response with health gate details
- **AND** the issue SHALL NOT be marked done/completed

### Requirement: health gate visibility in API responses

API responses that expose stage execution or merge completion details SHALL preserve health gate command, duration, concise summary, enabled status, and log excerpt.

#### Scenario: Stage execution includes health gate details
- **WHEN** a client requests issue details or stage execution data that includes check results
- **THEN** health gate check results SHALL include their structured output fields
- **AND** clients SHALL be able to distinguish health gate failure from awaiting user approval

#### Scenario: Approval state only appears after health gates pass
- **WHEN** an approval stage has an enabled health gate that has not passed
- **THEN** API responses SHALL NOT report that stage as awaiting user approval
- **AND** the health gate failure or running state SHALL be visible instead
