# OpenSpec Capability: ralph-task-execution

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
- **AND** if passed, updates tasks.json: passes=true
- **AND** if failed, captures error details for retry logic

### Requirement: Loop back from check to build

The system SHALL support looping back from check stage to build stage if issues are found.

#### Scenario: Fix issues in check stage
- **WHEN** check stage finds issues (test failures, etc.)
- **AND** user approves going back to build
- **THEN** the system transitions back to build stage
- **AND** the agent can append new tasks to tasks.json
- **AND** continues the build loop

### Requirement: Ralph-style task loop execution

The system SHALL preserve Ralph Build execution as a sequential compatibility loop while also exposing the same dynamic Build work as ordered executable tasks that can be executed one task at a time.

#### Scenario: Legacy loop preserves ordered Build execution
- **WHEN** legacy Build callers invoke the Ralph compatibility path
- **THEN** the system reads `tasks.json`
- **AND** validates dependencies before execution starts
- **AND** identifies pending tasks in ascending order
- **AND** executes tasks one at a time until all executable work completes or a failure stops the loop

#### Scenario: Single Build task can execute through shared task runtime
- **WHEN** Build runtime requests one specific pending task
- **THEN** the system loads the ordered executable task list from `tasks.json`
- **AND** selects the requested task without changing task order semantics for other tasks
- **AND** executes only that task through a single-task handler
- **AND** returns a normalized task result that the runner or aggregate can consume

### Requirement: Task failure handling with retry

The system SHALL keep task-owned retry and failure classification behavior when Build task execution is split into loader and handler boundaries.

#### Scenario: Retryable task failure remains handler-owned
- **WHEN** a Build task fails for a retryable reason such as unmet acceptance criteria or environment failure
- **THEN** the task handler classifies the failure using the existing Ralph failure categories
- **AND** stores failure learning for the attempt
- **AND** retries according to the existing category-based retry policy
- **AND** only pauses or fails after the task-owned retry policy is exhausted

#### Scenario: Non-retryable task failure still stops Build work
- **WHEN** a Build task fails for a non-retryable dependency or unrecoverable failure reason
- **THEN** the current task is marked failed
- **AND** later Build tasks do not execute automatically in that loop run
- **AND** the failure remains available for user-action or workflow reporting paths

### Requirement: Task status persistence

The system SHALL persist Build task progress using the same `tasks.json` schema and compatibility exports after the Ralph runtime is split.

#### Scenario: Split runtime preserves tasks.json progress semantics
- **WHEN** a Build task succeeds or fails through the split loader and handler path
- **THEN** the system updates the same `passes`, `attempts`, `error`, and duration progress fields in `tasks.json`
- **AND** compatibility helpers for reading, sorting, and locating pending tasks continue to operate on that file format
- **AND** the split runtime does not require a schema change to `tasks.json`

### Requirement: REQ-RTE-001 Task attempts consume session failure results

Build task execution SHALL treat session liveness failure as a failed task attempt even when task execution is performed through a single-task handler rather than only through the legacy Ralph loop.

#### Scenario: Session failure remains task-owned in split execution
- **WHEN** a single Build task execution receives a session failure result
- **THEN** the current task attempt is recorded as failed
- **AND** the task is not marked passed from partial output alone
- **AND** retry, pause, or failure handling is decided by Ralph task policy rather than by the session runtime itself

### Requirement: Build dynamic tasks execute through config-driven work source

The Build stage SHALL consume Ralph dynamic tasks through the configured Build work source and Ralph task handler when running under the config-driven stage runner. The system SHALL NOT introduce a separate temporary Build-only execution loop that bypasses WorkflowRun task selection or the shared task runtime.

#### Scenario: Build materializes Ralph tasks before selection

- **WHEN** Build starts or resumes under the config-driven runner and `tasks.json` contains executable Ralph tasks
- **THEN** the Build work source SHALL materialize those tasks into the Build StageRun
- **AND** WorkflowRun SHALL select the next Build task from the materialized StageRun task list

#### Scenario: Build task executes through Ralph handler

- **WHEN** WorkflowRun selects a Build task materialized from Ralph task data
- **THEN** the config-driven runner SHALL execute that task through the Ralph task handler
- **AND** the handler result SHALL update the corresponding WorkflowRun task state
- **AND** later Build work SHALL be selected by WorkflowRun rather than by a runner-local loop

### Requirement: Build migration preserves Ralph resume and checkpoint behavior

The config-driven Build path SHALL preserve existing Ralph task execution, retry, checkpoint, and materialization semantics while moving orchestration under the generic runner.

#### Scenario: Build resumes from materialized task state

- **WHEN** Build resumes after interruption or failed task recovery
- **THEN** the config-driven path SHALL use WorkflowRun task state and compatible checkpoint data to continue from the correct pending or failed Build task
- **AND** it SHALL NOT duplicate tasks that were already materialized from `tasks.json`

#### Scenario: Aggregate single Build task execution remains supported

- **WHEN** aggregate workflow execution requests one specific Build task
- **THEN** the config-driven runner SHALL execute only that requested Build task through the Ralph handler
- **AND** it SHALL report that task result before WorkflowRun selects any subsequent Build task or health check

### Requirement: Build health repair remains ordinary task work

The config-driven Build path SHALL run the Build health gate as a configured check after required Build tasks complete, and any allowed health fix SHALL be scheduled and executed as ordinary task work.

#### Scenario: Build health failure schedules configured fix task

- **WHEN** all required Build tasks have completed and `health:build` fails with an applicable repair policy
- **THEN** WorkflowRun SHALL append the configured Build health repair task to the Build StageRun
- **AND** the config-driven runner SHALL execute the repair task through the shared task runtime before `health:build` is evaluated again

#### Scenario: Build health remains blocked by failed tasks

- **WHEN** any Build task has failed or is not terminal
- **THEN** WorkflowRun SHALL NOT select `health:build`
- **AND** the Build stage SHALL remain blocked by the task state rather than by runner-local health gate control flow

