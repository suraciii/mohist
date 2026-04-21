## MODIFIED Requirements

### Requirement: Ralph-style task loop execution
The system SHALL execute tasks from tasks.json in a loop, one at a time, until all are complete. The system SHALL log task state at loop entry and exit.

**Loop Driver:** Mohist Main-agent (not a single long-running coder process)

#### Scenario: Execute pending tasks sequentially
- **WHEN** the build stage starts
- **THEN** the main-agent reads tasks.json
- **AND** logs at INFO level: total tasks, pending tasks, passed tasks
- **AND** identifies pending tasks (passes: false)
- **AND** selects the task with lowest order/priority
- **AND** assembles complete context (proposal + design + spec + learnings)
- **AND** calls `spawn_coder` with the assembled prompt
- **AND** waits for coder to complete
- **AND** verifies AC satisfaction
- **AND** updates passes/attempts/error in tasks.json
- **AND** repeats until all tasks are complete

#### Scenario: No pending tasks on loop entry
- **WHEN** the ralph loop starts and `findNextPendingTask` returns null immediately
- **THEN** the system logs at WARN level: "No pending tasks found — all N tasks have passes=true"
- **AND** returns `{ completed: 0, failed: 0, total: N, success: true }`

### Requirement: Task failure handling with retry
The system SHALL handle task failures with categorized retry logic. The system SHALL log each task attempt with its result.

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
  1. Logs at WARN level: task id, attempt number, failure category, error summary
  2. Extracts failure reason: "Only frontend validation implemented"
  3. Stores learning with failure context
  4. If attempts < 3:
     - Assembles retry prompt with failure context
     - Calls spawn_coder again
  5. If attempts >= 3:
     - Pauses build
     - Asks user: retry, skip, or abort

### Requirement: EventBus error handling
The system SHALL log errors when event emission fails.

#### Scenario: EventBus emit fails
- **WHEN** `eventBus.emit()` throws an error
- **THEN** the system logs at WARN level: event type, error message
- **AND** continues execution (fire-and-forget semantics preserved)
- **AND** does NOT crash the pipeline

#### Scenario: emitPersistent wrapper
- **WHEN** using `emitPersistent(event, data, { issueId, sessionId, workflowLogRepo })`
- **THEN** the system emits the event via EventBus
- **AND** writes the event to workflow_log
- **AND** catches and logs any errors from either operation
