## MODIFIED Requirements

### Requirement: retryable-current-stage-rejection

WorkflowRun SHALL expose current-stage retryability using the same semantics as `retryStage(stage)` without mutating or persisting state during retryability evaluation. Approval rejection of the current stage SHALL remain a retryable failed-run state when `retryStage(stage)` accepts the same stage.

#### Scenario: Failed current stage is retryable
- **GIVEN** the latest WorkflowRun has `status=failed`
- **AND** its `currentStage` equals the issue's current stage
- **AND** the current StageRun has `status=failed` because approval was rejected
- **WHEN** resume-pipeline evaluates retryability for that stage
- **THEN** the run SHALL be considered retryable
- **AND** no WorkflowRun state SHALL be changed by the evaluation

#### Scenario: Non-current stage is not retryable
- **GIVEN** the latest WorkflowRun has `status=failed`
- **AND** its `currentStage` differs from the issue's current stage
- **WHEN** resume-pipeline evaluates retryability for the issue stage
- **THEN** the run SHALL NOT be considered retryable
- **AND** the retry SHALL NOT be started

#### Scenario: Non-failed run is not retryable
- **GIVEN** the latest WorkflowRun has a status other than `failed`
- **WHEN** resume-pipeline evaluates retryability for the issue stage
- **THEN** the run SHALL NOT be considered a retryable current-stage failure

### Requirement: stage-approval-rejection-feedback

Rejecting a stage approval SHALL persist the user's rejection feedback in WorkflowRun history as rejection response data. Existing approval request context MAY be retained for audit, but it SHALL NOT replace or hide the user's rejection feedback.

#### Scenario: Rejection message is recorded
- **GIVEN** an issue is awaiting current-stage approval
- **WHEN** the user rejects the approval with feedback text
- **THEN** the WorkflowRun rejected approval state SHALL include that feedback
- **AND** the WorkflowRun failure evidence SHALL remain traceable to `approval-rejected`

#### Scenario: Prior approval context does not shadow feedback
- **GIVEN** an awaiting approval already has approval request output
- **WHEN** the user rejects the approval with different feedback
- **THEN** the persisted rejection response SHALL expose the user's feedback
- **AND** the prior approval request output MAY only appear as separate context
