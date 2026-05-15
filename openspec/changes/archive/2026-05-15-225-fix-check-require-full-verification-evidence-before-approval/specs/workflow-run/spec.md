## MODIFIED Requirements

### Requirement: Check full verification evidence

The Check StageRun SHALL include a first-class full verification check before review and mergeability checks. The verification check SHALL be persisted as `health:check` or a compatible stable check name and SHALL carry evidence for the candidate implementation it verified.

#### Scenario: Check stage is seeded with verification check

- **WHEN** a WorkflowRun creates or materializes the Check StageRun
- **THEN** the Check StageRun SHALL include `health:check` before `review-passed` and `merge-ready`
- **AND** `health:check` SHALL be visible as normal StageRun check state

#### Scenario: Passing verification evidence is persisted

- **WHEN** Check full verification passes
- **THEN** the Check StageRun SHALL persist a passing check result for `health:check`
- **AND** the result SHALL include command, status, duration, summary or message, and candidate snapshot metadata

#### Scenario: Failing verification evidence is persisted

- **WHEN** Check full verification fails or times out
- **THEN** the Check StageRun SHALL persist a failed check result for `health:check`
- **AND** the result SHALL include command, status, duration, summary, and a useful bounded log excerpt
- **AND** later Check approval evidence SHALL NOT be created for that failed candidate

### Requirement: Check candidate evidence invalidation

Candidate-changing Check work SHALL invalidate verification evidence together with review, merge-ready, and approval evidence.

#### Scenario: Candidate change invalidates Check evidence

- **WHEN** Check-stage work changes the candidate implementation after `health:check` has passed
- **THEN** the system SHALL invalidate or reset `health:check`
- **AND** it SHALL invalidate or reset dependent review, merge-ready, and approval state for the old candidate
