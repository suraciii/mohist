## ADDED Requirements

### Requirement: Stage SHALL NOT regress on pipeline failure
When a pipeline stage fails (build, plan, or review), the issue stage SHALL remain at the current stage and the status SHALL be set to `blocked`. The system SHALL NOT reset the stage to `Draft`.

#### Scenario: Build stage fails
- **WHEN** the build stage completes with a failed result
- **THEN** the issue stage SHALL remain `build` and status SHALL be `blocked`

#### Scenario: Plan stage fails
- **WHEN** the plan stage fails
- **THEN** the issue stage SHALL remain `plan` and status SHALL be `blocked`

### Requirement: Orphan recovery SHALL preserve issue stage
When the server restarts and recovers orphaned issues (issues with active agents that died during restart), the system SHALL preserve the current stage and set status to `blocked`.

#### Scenario: Server restart during build stage
- **WHEN** the server restarts while an issue is in the build stage with an active agent
- **THEN** the issue SHALL have stage=`build` and status=`blocked` after recovery

### Requirement: Reopen SHALL preserve current stage for blocked issues
When a blocked issue is reopened, the system SHALL preserve the current stage and set status to `active`. The user can then retry from the current stage.

#### Scenario: Reopen a blocked issue at build stage
- **WHEN** a blocked issue at stage=`build` is reopened
- **THEN** the issue SHALL have stage=`build` and status=`active`

### Requirement: Skip-to-review SHALL create complete approval gate
The skip-to-review endpoint SHALL set the approval state to `awaiting` and emit the `approval_requested` event, enabling the user to approve without requiring an agent run.

#### Scenario: Skip to review creates approval gate
- **WHEN** a user calls skip-to-review on an issue
- **THEN** the issue SHALL have stage=`review`, status=`active`, approvalState with status=`awaiting`, and an `approval_requested` event SHALL be emitted

#### Scenario: Approve after skip-to-review
- **WHEN** a user approves an issue that was skip-to-reviewed
- **THEN** the approval SHALL succeed and the pipeline SHALL proceed to merge-back and done
