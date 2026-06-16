## MODIFIED Requirements

### Requirement: stage-approval-rejection-feedback

When a user requests changes at a stage approval gate, the system SHALL create an `ApprovalFeedback` record with the user's feedback body scoped to the workflow run and stage. The stage SHALL resume as running work rather than being marked as failed. The feedback record SHALL be visible in workflow approval history. Prior approval request context MAY be retained for audit, but it SHALL NOT replace or hide the user's feedback.

#### Scenario: Feedback request creates ApprovalFeedback and resumes stage

- **GIVEN** an issue is awaiting current-stage approval
- **WHEN** the user requests changes with feedback text
- **THEN** the system SHALL create an `ApprovalFeedback` record with the user's body, scoped to the WorkflowRun and current stage
- **AND** the StageRun SHALL resume as running
- **AND** the WorkflowRun SHALL NOT record failure evidence for `approval-rejected`
- **AND** the `apply-feedback` task SHALL be scheduled as the next work item

#### Scenario: Prior approval context does not shadow feedback

- **GIVEN** an awaiting approval already has approval request output
- **WHEN** the user requests changes with different feedback
- **THEN** the persisted `ApprovalFeedback` record SHALL expose the user's feedback
- **AND** the prior approval request output MAY only appear as separate context in approval history

#### Scenario: Feedback request is distinct from rejection failure

- **GIVEN** the user requests changes with feedback
- **WHEN** the workflow state is inspected
- **THEN** the WorkflowRun SHALL NOT have `status = failed` solely because feedback was requested
- **AND** the stage failure reason SHALL NOT be set to `approval-rejected`
- **AND** the previous approval evidence SHALL be invalidated when feedback changes the candidate

### Requirement: retryable-current-stage-rejection

WorkflowRun SHALL expose current-stage retryability using the same semantics as `retryStage(stage)` without mutating or persisting state during retryability evaluation. When the current stage is in a feedback loop (awaiting approval, feedback requested, apply-feedback pending or running), the stage SHALL NOT be considered in a failed state that requires retry. Retryability SHALL NOT apply to active feedback loop stages.

#### Scenario: Active feedback loop is not retryable

- **GIVEN** the current StageRun is running the `apply-feedback` task after a feedback request
- **WHEN** resume-pipeline evaluates retryability for that stage
- **THEN** the run SHALL NOT be considered a retryable failure
- **AND** no retry SHALL be started for the feedback loop

#### Scenario: Non-current stage is not retryable

- **GIVEN** the latest WorkflowRun has `status = failed`
- **AND** its `currentStage` differs from the issue's current stage
- **WHEN** resume-pipeline evaluates retryability for the issue stage
- **THEN** the run SHALL NOT be considered retryable
- **AND** the retry SHALL NOT be started

#### Scenario: Non-failed run is not retryable

- **GIVEN** the latest WorkflowRun has a status other than `failed`
- **WHEN** resume-pipeline evaluates retryability for the issue stage
- **THEN** the run SHALL NOT be considered a retryable current-stage failure

## ADDED Requirements

### Requirement: apply-feedback task is a normal WorkflowRun task

The `apply-feedback` task SHALL be scheduled as an ordinary WorkflowRun task in the current StageRun with `causedBy` metadata referencing the feedback id. It SHALL participate in normal task ordering, status transitions, and completion guards.

#### Scenario: Feedback task appears in StageRun task list

- **WHEN** a user requests changes at an approval gate
- **THEN** the current StageRun SHALL append an `apply-feedback` task
- **AND** the task SHALL carry `causedBy` metadata with the feedback id
- **AND** the task SHALL appear before later checks and approval in the task ordering

#### Scenario: Feedback task failure blocks stage completion

- **WHEN** the `apply-feedback` task fails
- **THEN** the current StageRun SHALL fail through normal task failure semantics
- **AND** later checks and approval SHALL NOT execute until the failure is addressed

#### Scenario: Feedback task completion invalidates stale checks

- **WHEN** the `apply-feedback` task completes successfully
- **AND** the task changed code or stage artifacts
- **THEN** dependent checks and prior approval evidence SHALL be invalidated
- **AND** checks SHALL rerun before approval can be requested again

### Requirement: Workflow run records feedback as structured evidence

Workflow runs SHALL preserve structured feedback evidence including the feedback id, body, status, resolution task id, and resolution summary as runtime evidence for the feedback loop cycle.

#### Scenario: Feedback evidence is queryable from WorkflowRun

- **WHEN** an `ApprovalFeedback` record exists for a WorkflowRun
- **THEN** the feedback id, stage, status, and body SHALL be accessible from WorkflowRun evidence
- **AND** the feedback SHALL be included in approval history projections

#### Scenario: Resolved feedback records resolution evidence

- **WHEN** an `apply-feedback` task completes with a resolution summary
- **THEN** the WorkflowRun SHALL record that the corresponding feedback has been resolved
- **AND** the resolution summary and resolution task id SHALL be preserved
