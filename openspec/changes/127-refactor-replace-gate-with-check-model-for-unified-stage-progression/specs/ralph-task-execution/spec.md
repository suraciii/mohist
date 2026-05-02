## MODIFIED Requirements

### Requirement: Ralph-style task loop execution

The Build stage SHALL execute tasks from tasks.json via the unified BaseStageRunner execution loop. Task execution is the Build stage's Task list; check execution (all-tasks-complete, code-compiles) is the Build stage's Check list.

**Loop Driver:** Mohist Main-agent (not a single long-running coder process)

#### Scenario: Execute pending tasks sequentially

- **WHEN** the build stage starts via BaseStageRunner
- **THEN** the main-agent reads tasks.json
- **AND** identifies pending tasks (passes: false)
- **AND** selects the task with lowest order/priority
- **AND** assembles complete context (proposal + design + spec + learnings)
- **AND** calls `spawn_coder` with the assembled prompt
- **AND** waits for coder to complete
- **AND** verifies AC satisfaction
- **AND** updates passes/attempts/error in tasks.json
- **AND** repeats until all tasks are complete
- **AND** after all tasks, BaseStageRunner runs Build's Check list: [all-tasks-complete, code-compiles]

### Requirement: Task failure handling with retry

The Build stage SHALL handle task failures with categorized retry logic. Task-level retries are part of the task execution loop; check-level retries use the Reaction model.

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
     - The `all-tasks-complete` check fails
     - The check's reaction (`retry-task` or `ask-user`) is triggered

#### Scenario: Handle non-retryable failure

- **WHEN** task fails due to "Cannot find auth module export"
- **AND** it's a dependency/code issue (not retryable)
- **THEN** main-agent immediately pauses
- **AND** asks user for guidance
- **AND** stores the dependency issue in learning

### Requirement: Loop back from check to build

The system SHALL support looping back from Check stage to Build stage via the `escalate` reaction when check-stage checks fail (e.g., build-test-passed fails after auto-fix exhaustion).

#### Scenario: Fix issues via escalation from Check to Build

- **WHEN** Check stage's `build-test-passed` check fails
- **AND** `auto-fix` reaction exhausts max attempts
- **THEN** the reaction upgrades to `escalate` targeting Build stage
- **AND** the system transitions back to Build stage
- **AND** the agent can append new tasks to tasks.json
- **AND** continues the Build task execution loop
