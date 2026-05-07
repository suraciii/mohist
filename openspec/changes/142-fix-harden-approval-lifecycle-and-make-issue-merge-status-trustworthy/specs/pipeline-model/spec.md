## MODIFIED Requirements

### Requirement: REQ-PM-001 Stage-aware approval drives pipeline checks

Approval state SHALL only satisfy a pipeline approval check when `approvalState.stage` equals the issue's current stage and `approvalState.status` matches the expected status.

#### Scenario: Stale Plan approval ignored in Check
- **GIVEN** an issue is in `check` stage
- **AND** `approvalState.stage` is `plan`
- **AND** `approvalState.status` is `approved`
- **WHEN** the `user-approval` check runs
- **THEN** the check SHALL NOT pass
- **AND** the Check stage SHALL request current-stage approval

#### Scenario: Current-stage approval passes
- **GIVEN** an issue is in `check` stage
- **AND** `approvalState.stage` is `check`
- **AND** `approvalState.status` is `approved`
- **WHEN** the `user-approval` check runs
- **THEN** the check SHALL pass

### Requirement: REQ-PM-002 Done requires successful merge

The pipeline SHALL NOT mark an issue `stage=done` or `status=completed` merely because Check tasks and checks passed. Done/completed SHALL only be written after the issue reaches `mergeState=merged`.

#### Scenario: Check approval queues merge
- **GIVEN** Check tasks and non-user checks have passed
- **AND** the issue is awaiting current-stage Check approval
- **WHEN** the user approves the issue
- **THEN** the issue SHALL be enqueued for merge
- **AND** the issue SHALL remain not completed until merge success

#### Scenario: Merge success completes issue
- **GIVEN** an issue is in `check` stage
- **AND** the merge queue completes successfully
- **WHEN** merge state becomes `merged`
- **THEN** the issue SHALL transition to `stage=done`
- **AND** the issue SHALL transition to `status=completed`
- **AND** consumed approval state SHALL be cleared
