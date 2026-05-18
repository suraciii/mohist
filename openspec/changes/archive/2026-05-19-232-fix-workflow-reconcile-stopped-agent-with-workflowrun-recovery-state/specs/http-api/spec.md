## ADDED Requirements

### Requirement: Issue APIs expose attempt-derived recovery projection

Issue detail, stage-state, queue-related, and recovery API responses SHALL expose or use a shared recovery projection derived from the reconciled latest work item attempt. The projection SHALL identify the current work item, latest attempt state, workflow recovery summary state, and allowed actions.

#### Scenario: Issue detail includes recovery projection

- **WHEN** a client requests issue detail for an issue with an active WorkflowRun
- **THEN** the API SHALL reconcile the latest running attempt if needed
- **AND** the response SHALL expose recovery data that includes current work item identity, latest attempt state, workflow summary state, and allowed actions

#### Scenario: Stage-state agrees with issue detail

- **WHEN** a client requests `GET /api/issues/:number/stage-state`
- **THEN** the API SHALL use the same reconciled latest attempt state as issue detail
- **AND** recovery action availability SHALL match issue detail for the same issue

### Requirement: Retry targets only failed latest work attempts

`POST /api/issues/:number/retry` SHALL succeed only when the reconciled latest current-stage work item attempt is `failed`. Interrupted, running, completed, or absent latest attempts SHALL NOT be accepted as failed retry targets.

#### Scenario: Failed task attempt is retryable

- **WHEN** the reconciled latest current-stage task attempt is `failed`
- **THEN** `POST /api/issues/:number/retry` SHALL reset the failed task and downstream dependent work using existing retry behavior
- **AND** the response SHALL indicate retry was accepted

#### Scenario: Failed check attempt is retryable

- **WHEN** the reconciled latest current-stage check attempt is `failed`
- **THEN** `POST /api/issues/:number/retry` SHALL reset the failed check and downstream dependent work using existing check retry behavior
- **AND** the response SHALL indicate retry was accepted

#### Scenario: Interrupted attempt rejects retry with guidance

- **WHEN** the reconciled latest work item attempt is `interrupted`
- **THEN** `POST /api/issues/:number/retry` SHALL return a 409-style conflict
- **AND** the response SHALL explain that interrupted work is not failed work
- **AND** the response SHALL suggest resume, rerun stage, or inspect actions when available

#### Scenario: Stale running attempt reconciles before retry decision

- **WHEN** the latest attempt is stored as `running` but has no live execution evidence
- **AND** a client requests retry
- **THEN** the API SHALL reconcile the attempt before checking retry eligibility
- **AND** retry SHALL be rejected or accepted according to the reconciled attempt state rather than raw WorkflowRun status

### Requirement: Rerun and resume use interrupted recovery semantics

Recovery APIs SHALL keep rerun and resume distinct from retry. Rerun stage SHALL create fresh attempts for stage work. Resume for interrupted work SHALL not pretend the interrupted attempt failed.

#### Scenario: Rerun stage creates fresh attempts

- **WHEN** a client requests rerun for the current stage after interruption
- **THEN** the API SHALL restart the stage work from the appropriate stage boundary
- **AND** new attempts SHALL be created as work items execute

#### Scenario: Resume interrupted work does not require failed run

- **WHEN** a client requests resume for an interrupted latest attempt
- **THEN** the API SHALL use interrupted recovery semantics
- **AND** it SHALL NOT require `WorkflowRun.status = failed` as if resume were retry
