## MODIFIED Requirements

### Requirement: agent-backed-rejected-approval-retry-session

When an agent-backed stage is retried after approval rejection, the retry SHALL be observable as a new stage execution attempt through existing runner and coder-session tracking. The retried attempt SHALL receive the recorded rejection feedback in its input context.

#### Scenario: Rejected Plan approval starts new session
- **GIVEN** an issue has a failed current-stage WorkflowRun due to rejected Plan approval
- **WHEN** `resume-pipeline` starts the retry for `Stage.name = "plan"`
- **THEN** a new Plan runner/coder session SHALL be started through existing session tracking
- **AND** the prior Plan session SHALL NOT be reused as the retry attempt

#### Scenario: Rejection feedback is in retry input
- **GIVEN** a Plan approval was rejected with feedback
- **WHEN** the Plan stage is retried
- **THEN** the retried Plan prompt or task input SHALL include the rejection feedback
- **AND** the agent SHALL be able to use that feedback while regenerating Plan artifacts

#### Scenario: Retried stage requests approval again
- **GIVEN** a rejected Plan stage retry has regenerated reviewable artifacts
- **WHEN** the Plan stage reaches its approval gate
- **THEN** the stage SHALL request approval again
- **AND** the retry SHALL NOT bypass approval because a prior approval was rejected
