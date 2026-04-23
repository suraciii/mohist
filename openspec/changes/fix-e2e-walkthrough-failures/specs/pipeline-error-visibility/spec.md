## ADDED Requirements

### Requirement: Pipeline error state storage
The system SHALL persist pipeline failure messages in the issue's `approvalState` field with `status: 'error'` when the pipeline fails.

#### Scenario: Pipeline throws an exception
- **WHEN** `executePipeline` catches an unexpected error
- **THEN** the system SHALL write `{ stage: <current>, status: 'error', output: { error: <message> } }` to the issue's `approvalState` before setting status to blocked

#### Scenario: Pipeline returns a failure result
- **WHEN** `pipeline.run()` returns a result with `completed: false`
- **THEN** the system SHALL write `{ stage: <result.stage>, status: 'error', output: { error: <result.message> } }` to the issue's `approvalState` before setting status to blocked

### Requirement: API exposes error state
The `/api/issues/:number` response SHALL include the error message from `approvalState` when `approvalState.status === 'error'`.

#### Scenario: Issue in blocked state with error
- **WHEN** a client requests an issue that has `approvalState.status: 'error'`
- **THEN** the response SHALL include `approvalState.output.error` containing the failure reason

### Requirement: CLI displays error state
The `mo issue show <number>` command SHALL display the error message when the issue's `approvalState.status === 'error'`.

#### Scenario: User checks a blocked issue
- **WHEN** the user runs `mo issue show <number>` for an issue with `approvalState.status: 'error'`
- **THEN** the output SHALL include an "Error" line showing the error message from `approvalState.output.error`
