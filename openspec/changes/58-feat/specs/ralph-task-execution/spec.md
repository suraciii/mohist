## MODIFIED Requirements

### Requirement: Task failure handling with retry
The system SHALL handle task failures with categorized retry logic.

**Failure Categories:**

| Type | Examples | Retry | Max Attempts |
|------|----------|-------|--------------|
| AC not met | Missing validation | Yes | 3 total |
| Environment | npm install failed | Yes | 2 total |
| Dependency | Can't find module | No | - |
| Timeout | >taskTimeout execution | No | - |

**Timeout Resolution:** Per-task timeout is derived from config:
- If `stageTimeoutMs` is set and tasks exist: `max(stageTimeoutMs / taskCount, MIN_TASK_TIMEOUT_MS)`
- Otherwise: `config.agent.taskTimeout * 1000`
- `MIN_TASK_TIMEOUT_MS` = 60 seconds (config minimum, was previously hardcoded at 10 minutes)

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

#### Scenario: Handle timeout with configurable task timeout
- **WHEN** a task execution exceeds the configured `agent.taskTimeout` duration
- **THEN** main-agent categorizes failure as "timeout"
- **AND** retry behavior follows the timeout failure category rules
- **AND** the timeout threshold is read from config, not hardcoded

#### Scenario: Handle non-retryable failure
- **WHEN** task fails due to "Cannot find auth module export"
- **AND** it's a dependency/code issue (not retryable)
- **THEN** main-agent immediately pauses
- **AND** asks user for guidance
- **AND** stores the dependency issue in learning
