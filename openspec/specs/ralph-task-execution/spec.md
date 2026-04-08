## ADDED Requirements

### Requirement: Ralph-style task loop execution
The system SHALL execute tasks from prd.json in a loop, one at a time, until all are complete.

**Loop Driver:** Mohist Main-agent (not a single long-running coder process)

#### Scenario: Execute pending tasks sequentially
- **WHEN** the build stage starts
- **THEN** the main-agent reads prd.json
- **AND** identifies pending tasks (status: "pending")
- **AND** selects the task with lowest order/priority
- **AND** assembles complete context (proposal + design + spec + learnings)
- **AND** calls `spawn_coder` with the assembled prompt
- **AND** waits for coder to complete
- **AND** verifies AC satisfaction
- **AND** updates task-status.json
- **AND** repeats until all tasks are complete

### Requirement: Task execution context assembly
The system SHALL assemble complete context for each task execution.

**Context Components:**
1. System prompt defining the agent role
2. proposal.md for background
3. design.md for technical constraints
4. The specific spec file referenced by task.spec
5. Session memories from previous tasks (insights + adjustments)
6. Task description and acceptanceCriteria

#### Scenario: Build task context
- **WHEN** executing task T-003
- **THEN** the main-agent assembles:
  ```
  [System] You are the Mohist Coder Agent...
  
  [Proposal] {proposal.md content}
  
  [Design] {design.md content}
  
  [Current Requirement] {specs/auth/spec.md content}
  
  [Previous Learnings]
  From T-001: "Project uses single quotes"
  From T-002: "Tests need docker"
  
  [Task T-003]
  Description: Implement login API
  AC:
  - POST /api/login returns JWT
  - Validates email format
  - Returns 401 for invalid credentials
  ```

### Requirement: Task result verification
The system SHALL verify that task execution meets the acceptance criteria.

#### Scenario: Verify task completion
- **WHEN** a task execution completes
- **THEN** the main-agent checks:
  1. Did coder report success?
  2. Does the implementation satisfy all AC?
  3. Run typecheck/tests if specified
- **AND** if passed, updates task-status.json: status="completed"
- **AND** if failed, captures error details for retry logic

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

### Requirement: Task status persistence
The system SHALL persist task execution status for recovery.

**File:** `{change-path}/task-status.json`

```json
{
  "current_task_index": 3,
  "total_tasks": 7,
  "tasks": [
    {"id": "T-001", "status": "completed", "attempts": 1},
    {"id": "T-002", "status": "completed", "attempts": 1},
    {"id": "T-003", "status": "failed", "attempts": 3, "error": "Missing backend validation"}
  ]
}
```

#### Scenario: Resume from failed task
- **WHEN** user runs `mo issue resume` after build failure
- **THEN** main-agent reads task-status.json
- **AND** identifies current_task_index (3, meaning T-003)
- **AND** loads learnings from T-001 and T-002
- **AND** continues execution from T-003

### Requirement: Loop back from check to build
The system SHALL support looping back from check stage to build stage if issues are found.

#### Scenario: Fix issues in check stage
- **WHEN** check stage finds issues (test failures, etc.)
- **AND** user approves going back to build
- **THEN** the system transitions back to build stage
- **AND** the agent can append new tasks to prd.json
- **AND** continues the build loop
