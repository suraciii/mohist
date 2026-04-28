## ADDED Requirements

### Requirement: Timeout task automatic retry
The system SHALL automatically retry task attempts that fail due to timeout, up to 2 retries (3 total attempts).

#### Scenario: Timeout triggers automatic retry
- **WHEN** a task attempt fails with a timeout error
- **AND** the task's total attempt count is less than 3
- **THEN** the Ralph executor automatically schedules a retry
- **AND** the failure category is determined by whether a WIP commit was created before timeout

#### Scenario: Timeout with WIP commit enables resume context
- **WHEN** a task times out and a WIP commit was successfully created before the agent was killed
- **THEN** the failure is categorized as `timeout_with_wip`
- **AND** the retry prompt includes a `wipResumeContext` describing the saved files and diff
- **AND** the agent is instructed to continue from the saved state, not re-implement files listed in the WIP

#### Scenario: Timeout without WIP commit is non-resumable
- **WHEN** a task times out and no WIP commit was created (or WIP commit failed)
- **THEN** the failure is categorized as `timeout`
- **AND** the retry prompt does not include `wipResumeContext`
- **AND** the agent starts fresh

#### Scenario: Max timeout retries exceeded
- **WHEN** a task attempt fails with timeout
- **AND** the task's total attempt count is already 3
- **THEN** the Ralph executor pauses the build
- **AND** requests user guidance (retry, skip, or abort)

### Requirement: Timeout retry uses existing WIP config
The system SHALL use the existing `timeout_with_wip` failure category configuration for timeout retry behavior.

#### Scenario: WIP commit retry respects existing maxAttempts
- **WHEN** a `timeout_with_wip` task retries
- **THEN** the effective max attempts for that category is 2 (per `FAILURE_CATEGORY_CONFIGS.timeout_with_wip`)
- **AND** the total attempts include the initial failed timeout attempt