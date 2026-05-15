## MODIFIED Requirements

### Requirement: Check full verification before review

The workflow engine SHALL run Check full verification before generating or reusing AI review as the current approval candidate. A failing or missing verification result SHALL stop Check before AI review, merge-ready, or user approval.

#### Scenario: Verification runs before AI review

- **WHEN** default Check execution starts for a candidate implementation
- **THEN** the system SHALL run the configured full verification gate before `ai-review`
- **AND** it SHALL NOT generate a new AI review before verification passes

#### Scenario: Verification failure blocks later Check work

- **WHEN** Check full verification fails
- **THEN** Check SHALL NOT run `ai-review`
- **AND** Check SHALL NOT run `merge-ready`
- **AND** Check SHALL NOT request user approval

#### Scenario: Verification pass allows review and mergeability

- **WHEN** Check full verification passes
- **THEN** Check MAY continue to `ai-review`, `review-passed`, `merge-ready`, and approval gating for the same candidate implementation

### Requirement: Check approval requires current verification

Check approval SHALL only be requested when full verification, AI review, and merge-ready evidence all pass for the same current candidate implementation.

#### Scenario: Missing verification blocks approval request

- **WHEN** Check reaches approval gating
- **AND** no current passing `health:check` evidence exists
- **THEN** the system SHALL NOT request Check approval
- **AND** it SHALL expose a blocking reason that verification evidence is missing

#### Scenario: Stale verification blocks approval request

- **WHEN** Check reaches approval gating
- **AND** passing verification evidence does not match the current approval candidate snapshot
- **THEN** the system SHALL NOT request Check approval
- **AND** it SHALL require Check verification to rerun for the current candidate

#### Scenario: Approval candidate includes verification evidence

- **WHEN** Check approval is requested
- **THEN** the approval candidate output SHALL include verification evidence, review verdict evidence, and merge-ready evidence
- **AND** all included evidence SHALL refer to the same candidate implementation
