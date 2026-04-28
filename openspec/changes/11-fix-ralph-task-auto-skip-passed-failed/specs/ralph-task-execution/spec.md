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

#### Scenario: Handle non-retryable failure without onAskUser (auto-skip)
- **WHEN** a task fails with a non-retryable error (e.g. timeout, dependency)
- **AND** `onAskUser` is not provided in the executor context
- **THEN** the system SHALL mark the task as `passes: false` in tasks.json
- **AND** the system SHALL set the task result status to `skipped`
- **AND** the system SHALL increment the `failed` counter
- **AND** the error SHALL be recorded as `Auto-skipped (no onAskUser): <original error>`
- **AND** the loop SHALL NOT report `success: true` for this result

### Requirement: Ralph-style task loop execution
The system SHALL execute tasks from tasks.json in a loop, one at a time, until all are complete.

**Loop Driver:** Mohist Main-agent (not a single long-running coder process)

**Loop Result:** The `RalphLoopResult` SHALL include a `skipped` counter tracking tasks that were auto-skipped due to non-retryable failures without user interaction available.

**Success Calculation:** The loop result `success` field SHALL be `true` only when `failed === 0` and `skipped === 0`.

#### Scenario: Execute pending tasks sequentially
- **WHEN** the build stage starts
- **THEN** the main-agent reads tasks.json
- **AND** identifies pending tasks (passes: false)
- **AND** selects the task with lowest order/priority
- **AND** assembles complete context (proposal + design + spec + learnings)
- **AND** calls `spawn_coder` with the assembled prompt
- **AND** waits for coder to complete
- **AND** verifies AC satisfaction
- **AND** updates passes/attempts/error in tasks.json
- **AND** repeats until all tasks are complete

#### Scenario: Loop reports failure when tasks are auto-skipped
- **WHEN** the Ralph loop completes with one or more auto-skipped tasks
- **THEN** the `RalphLoopResult` SHALL have `skipped > 0`
- **AND** `success` SHALL be `false`
- **AND** `failed` SHALL include the count of auto-skipped tasks
