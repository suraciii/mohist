## MODIFIED Requirements

### Requirement: Task failure handling with retry
The system SHALL handle task failures with categorized retry logic.

**Failure Categories:**

| Type | Examples | Retry | Max Attempts |
|------|----------|-------|--------------|
| AC not met | Missing validation | Yes | 3 total |
| Environment | npm install failed | Yes | 2 total |
| Dependency | Can't find module | No | - |
| Timeout | >30min execution | No | - |
| Timeout with WIP | >30min but progress saved | Yes | 2 total |
| Hang unrecoverable | LLM stream hang, recovery failed | No | - |

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

#### Scenario: Handle hang unrecoverable failure
- **WHEN** task fails with error containing `[HANG_UNRECOVERABLE]`
- **THEN** `categorizeFailure()` SHALL return `'hang_unrecoverable'`
- **AND** the failure is non-retryable (`retryable: false`, `maxAttempts: 1`)
- **AND** main-agent immediately pauses and asks user for guidance
