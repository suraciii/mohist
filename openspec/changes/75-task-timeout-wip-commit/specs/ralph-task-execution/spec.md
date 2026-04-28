## MODIFIED Requirements

### Requirement: Task failure handling with retry

The system SHALL handle task failures with categorized retry logic.

**Failure Categories:**

| Type | Examples | Retry | Max Attempts |
|------|----------|-------|--------------|
| AC not met | Missing validation | Yes | 3 total |
| Environment | npm install failed | Yes | 2 total |
| Dependency | Can't find module | No | - |
| Timeout | >30min execution | Yes | 3 total |

#### Scenario: Handle AC failure with retry
- **WHEN** task T-003 fails because AC "backend validation" not met
- **THEN** main-agent:
  1. Extracts failure reason: "Only frontend validation implemented"
  2. Stores learning with failure context
  3. If attempts < 3:
     - Assembles retry prompt with failure context
     - Calls spawn_coder again
  4. If attempts >= 3:
     - Pauses build
     - Asks user: retry, skip, or abort

#### Scenario: Handle non-retryable failure
- **WHEN** task fails due to "Cannot find auth module export"
- **AND** it's a dependency/code issue (not retryable)
- **THEN** main-agent immediately pauses
- **AND** asks user for guidance
- **AND** stores the dependency issue in learning

#### Scenario: Handle timeout failure with automatic retry
- **WHEN** task T-003 times out
- **AND** total attempts for this task is less than 3
- **THEN** main-agent:
  1. Records the actual elapsed duration in `task.durations`
  2. Creates WIP commit via `onBeforeKill` hook if worktreeManager is configured
  3. Categorizes failure as `timeout` or `timeout_with_wip` based on WIP commit success
  4. If `timeout_with_wip`: builds `wipResumeContext` with changed files and diff summary
  5. Assembles retry prompt including `wipResumeContext` if available
  6. Schedules automatic retry up to max attempts

#### Scenario: Handle timeout failure exhausting retries
- **WHEN** task T-003 times out
- **AND** total attempts for this task equals 3
- **THEN** main-agent:
  1. Records the actual elapsed duration in `task.durations`
  2. Pauses the build
  3. Asks user: retry (override max), skip, or abort