### Requirement: Retry availability and execution use one failure target
A failed workflow run SHALL determine its retry target once and SHALL use that determination both to expose the retry action and to perform the retry. A task failure, including a persisted legacy context-exhaustion failure, SHALL resolve to the failed task; a check failure SHALL resolve to the failed check. A failure with no retry target SHALL NOT expose a retry action and retry execution SHALL reject it.

#### Scenario: Legacy task failure exposes the executable retry
- **WHEN** a failed workflow run carries a legacy context-exhaustion failure for a failed task
- **THEN** its available actions SHALL include retry for that task
- **AND** retry SHALL create a new attempt of that same task

#### Scenario: Failed check exposes the executable retry
- **WHEN** a failed workflow run identifies a failed check
- **THEN** its available actions SHALL include retry for that check
- **AND** retry SHALL rerun that same check

#### Scenario: Failure without a retry target remains non-retryable
- **WHEN** a failed workflow run has a failure that does not identify retryable task or check work
- **THEN** its available actions SHALL NOT include retry
- **AND** retry SHALL reject the request without changing the workflow run

### Requirement: Failed-workflow controls retain their existing outcomes
Retry, rerun, and rerun-from-stage SHALL retain their existing workflow-control semantics. Retrying a retryable failed task or check SHALL resume dispatch from that work, while rerun and rerun-from-stage SHALL remain available independently of retry-target availability.

#### Scenario: Retryable failure retains rerun controls
- **WHEN** a workflow run fails at retryable work
- **THEN** its available actions SHALL include retry and rerun
- **AND** rerun-from-stage SHALL continue to accept reached stages under its existing eligibility rules

### Requirement: Recovery continuation allowance is runner-owned execution state
The control plane SHALL preserve a recovery follow-up's numeric `recoveryRemaining` value without enforcing its declared recovery-budget range. It SHALL reject a recovery follow-up that omits numeric continuation state or a follow-up that carries continuation state without a recovery declaration. The runner SHALL bound a numeric remaining allowance below zero to zero and one above the declared budget to the declared budget before selecting automatic recovery work.

#### Scenario: Above-budget continuation reaches runner evaluation
- **WHEN** a recovery follow-up carries a numeric `recoveryRemaining` greater than its declared budget
- **THEN** the control plane SHALL accept and preserve that value for dispatch
- **AND** the runner SHALL evaluate the attempt with the declared budget as its effective remaining allowance

#### Scenario: Negative continuation cannot recover automatically
- **WHEN** a recovery follow-up carries a negative numeric `recoveryRemaining`
- **THEN** the control plane SHALL accept and preserve that value for dispatch
- **AND** the runner SHALL evaluate the attempt with zero remaining allowance and SHALL schedule no automatic recovery follow-up

#### Scenario: Malformed continuation remains rejected
- **WHEN** a recovery follow-up omits `recoveryRemaining` or a non-recovery follow-up supplies it
- **THEN** the control plane SHALL reject the malformed follow-up
- **AND** it SHALL NOT create a new task attempt from that follow-up
