## MODIFIED Requirements

### Requirement: REQ-RTE-001 Task attempts consume session failure results

Build task execution SHALL treat session liveness failure as a failed session call result and apply existing task failure policy outside the session layer.

#### Scenario: Session failed result fails task attempt
- **WHEN** an opencode session call returns `success=false` with session failure metadata
- **THEN** the current task attempt SHALL be recorded as failed
- **AND** the failure reason SHALL be available to retry/block/user-action policy

#### Scenario: Session failure does not complete task
- **WHEN** a session fails before normal completion
- **THEN** the task SHALL NOT be marked passed solely because the session produced partial text or previous events

#### Scenario: Retry policy remains task-owned
- **WHEN** a task attempt fails because the session failed
- **THEN** task/workflow policy MAY retry, block, or request user action
- **AND** the session runtime SHALL NOT choose the workflow recovery strategy
