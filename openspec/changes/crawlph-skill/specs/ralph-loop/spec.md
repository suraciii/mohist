## ADDED Requirements

### Requirement: Infinite Retry Loop

The system SHALL implement infinite retry loop for Issue processing until success.

#### Scenario: Retry on failure

- **WHEN** a sub-agent fails to process an Issue
- **THEN** system SHALL spawn a new sub-agent with clean context
- **AND** system SHALL retry until success

#### Scenario: Clean context per iteration

- **WHEN** spawning a new sub-agent
- **THEN** the sub-agent SHALL start with a clean context
- **AND** the sub-agent SHALL NOT inherit context from previous attempts

#### Scenario: Progress persistence

- **WHEN** a sub-agent makes progress before failing
- **THEN** system SHALL persist the progress to external files
- **AND** the next sub-agent SHALL be able to resume from the persisted progress

#### Scenario: Progress file format

- **WHEN** persisting progress
- **THEN** system SHALL write to `/data/.clawdbot/crawlph-progress/issue-{N}.json`
- **AND** file SHALL include:
  - `currentStage`: current workflow stage
  - `attempts`: number of retry attempts
  - `prNumber`: associated PR number (if created)
  - `lastError`: last error message
  - `checkpoints`: map of stage → completion timestamp
  - `context`: additional context (branch name, spec file path)

#### Scenario: Timeout handling

- **WHEN** a sub-agent exceeds timeout (default: 30 minutes)
- **THEN** system SHALL terminate the sub-agent
- **AND** system SHALL treat it as a failure and retry
- **AND** timeout SHALL be configurable via `--timeout` parameter

#### Scenario: User input required

- **WHEN** sub-agent returns `NEEDS_USER_INPUT` status
- **THEN** system SHALL pause retry loop
- **AND** system SHALL send Channel notification requesting user action
- **AND** system SHALL wait for user action before resuming

#### Scenario: Completion detection

- **WHEN** sub-agent returns `SUCCESS` status
- **THEN** system SHALL clean up progress file
- **AND** system SHALL mark Issue as processed
- **AND** orchestrator SHALL record in PROCESSED_ISSUES set

### Requirement: Orchestrator Context Hygiene

The orchestrator SHALL maintain minimal context to avoid accumulation.

#### Scenario: Retain only essential state

- **WHEN** completing an iteration
- **THEN** orchestrator SHALL retain only:
  - PROCESSED_ISSUES (set of Issue numbers)
  - OPEN_PRS (list of PR numbers)
  - FAILED_ISSUES (map of Issue to failure count)
  - Configuration parameters

#### Scenario: Clear transient data

- **WHEN** completing an iteration
- **THEN** orchestrator SHALL clear:
  - Issue bodies
  - Comment bodies
  - Sub-agent transcripts
  - Codebase analysis results

### Requirement: Failure Detection

The system SHALL detect patterns of persistent failure.

#### Scenario: Detect consecutive failures

- **WHEN** an Issue fails more than 10 consecutive times
- **THEN** system SHALL send a warning notification to Channel
- **AND** system SHALL add `stage:blocked` label to Issue
- **AND** system SHALL suggest manual intervention in Issue comment

#### Scenario: Allow manual intervention

- **WHEN** user adds stage:blocked label to an Issue
- **THEN** system SHALL skip that Issue in future iterations
- **AND** system SHALL NOT attempt to process it

#### Scenario: Resume after unblock

- **WHEN** user removes stage:blocked label
- **THEN** system SHALL resume processing on next iteration
- **AND** system SHALL reset failure counter to 0
