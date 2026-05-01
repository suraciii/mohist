## ADDED Requirements

### Requirement: recoverIssues cleans up orphaned coder_session rows

When `AgentRunnerService.recoverIssues()` marks an active issue as `interrupted` on server restart, the system SHALL also clean up all `coder_session` rows for that issue with `status=running` by setting them to `status=failed`.

#### Scenario: Server restart with orphaned coder_session

- **WHEN** server restarts and `recoverIssues()` finds an active issue
- **THEN** the issue is marked as `interrupted`
- **AND** all `coder_session` rows for that issue with `status=running` are updated to `status=failed`
- **AND** no `coder_session` rows remain with `status=running` for interrupted issues

#### Scenario: Server restart with completed coder_sessions

- **WHEN** server restarts and an issue has `coder_session` rows with `status=completed` or `status=failed`
- **THEN** those rows are NOT modified
- **AND** only `status=running` rows are cleaned up

### Requirement: executePipeline confirms issue status is active

When `executePipeline()` starts executing a pipeline for an issue, it SHALL explicitly set the issue `status` to `active` before beginning agent execution, regardless of the current status value.

#### Scenario: Pipeline starts after reopen

- **WHEN** `executePipeline()` is called (e.g. after reopen → resumePipeline)
- **THEN** the system SHALL call `issueRepo.updateStatus(issue.id, IssueStatus.Active)` before agent execution begins
- **AND** if the DB write fails, the system SHALL log a warning but continue execution

#### Scenario: Pipeline starts with status already active

- **WHEN** `executePipeline()` is called and the issue status is already `active`
- **THEN** the redundant `updateStatus` call succeeds without side effects
