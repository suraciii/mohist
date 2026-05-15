## MODIFIED Requirements

### Requirement: blocked-retryable-current-stage-resume

The pipeline queue SHALL treat a blocked issue as runnable by `resume-pipeline` only when the latest WorkflowRun represents a retryable failed run at the issue's current stage. Blocked issues without a retryable current-stage failed WorkflowRun SHALL remain skipped or non-runnable.

#### Scenario: Retryable blocked current-stage failure runs
- **GIVEN** an issue has `status=blocked`
- **AND** the issue's current stage is `plan`
- **AND** the latest WorkflowRun has `status=failed` and `currentStage=plan`
- **AND** WorkflowRun retryability accepts retrying `plan`
- **WHEN** a `resume-pipeline` queue task is evaluated
- **THEN** the queue task SHALL NOT complete as `skipped` solely because the issue is blocked
- **AND** normal pipeline execution SHALL begin so the WorkflowRun retry for `plan` can start

#### Scenario: Genuinely blocked issue remains skipped
- **GIVEN** an issue has `status=blocked`
- **AND** there is no latest WorkflowRun that is retryable for the issue's current stage
- **WHEN** a `resume-pipeline` queue task is evaluated
- **THEN** the queue task SHALL remain skipped or non-runnable according to existing blocked-issue behavior
- **AND** the issue SHALL NOT be broadly unblocked

#### Scenario: Approved approval continuation still resumes
- **GIVEN** an issue has `status=blocked`
- **AND** the issue has current-stage approved approval state
- **WHEN** a `resume-pipeline` queue task is evaluated
- **THEN** the existing approved-continuation path SHALL still clear blocked state and resume the pipeline

### Requirement: rejected-approval-resume-regression

Regression coverage SHALL prove that approval rejection enqueues runnable same-stage retry work and that non-retryable blocked issues remain non-runnable.

#### Scenario: Rejected Plan approval starts same-stage retry
- **GIVEN** an issue is awaiting approval at `Stage.name = "plan"`
- **WHEN** the approval is rejected with feedback
- **AND** the queued `resume-pipeline` task runs
- **THEN** the task SHALL start a retry of the `plan` stage instead of completing as `skipped`
- **AND** the retried stage SHALL request approval again after regenerated artifacts are ready

#### Scenario: Non-retryable blocked resume remains skipped
- **GIVEN** an issue is blocked without a retryable latest current-stage failed WorkflowRun
- **WHEN** a `resume-pipeline` task runs
- **THEN** the task SHALL complete as skipped or leave the issue non-runnable
- **AND** no new same-stage attempt SHALL be started
