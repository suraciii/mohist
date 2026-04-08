## ADDED Requirements

### Requirement: Ralph-style task loop execution
The system SHALL execute tasks from prd.json in a loop, one at a time, until all are complete.

#### Scenario: Execute pending tasks sequentially
- **WHEN** the build stage starts
- **THEN** the system reads prd.json
- **AND** identifies pending tasks (passes: false)
- **AND** selects the task with lowest priority number
- **AND** executes the task
- **AND** updates prd.json with the result
- **AND** repeats until all tasks are complete

### Requirement: Task execution context assembly
The system SHALL assemble complete context for each task execution, including design docs and session memories.

#### Scenario: Build task context
- **WHEN** executing a task from prd.json
- **THEN** the system assembles the prompt context:
  - System prompt defining the agent role
  - proposal.md for background
  - design.md for technical constraints
  - The specific spec file referenced by task.spec
  - Session memories from previous tasks
  - Task description and acceptanceCriteria

### Requirement: Task result verification
The system SHALL verify that task execution meets the acceptance criteria.

#### Scenario: Verify task completion
- **WHEN** a task execution completes
- **THEN** the system checks if acceptance criteria are met
- **AND** runs typecheck/tests if specified
- **AND** if passed, sets prd.json task.passes = true
- **AND** if failed, stores failure reason and asks user whether to retry or continue

### Requirement: Task failure handling
The system SHALL handle task failures gracefully with options to retry, skip, or abort.

#### Scenario: Handle failed task
- **WHEN** a task fails verification
- **THEN** the system captures the error details
- **AND** stores learning in session memory
- **AND** asks user: retry, skip, or abort the build
- **AND** if retry, adjusts task prompt with failure context and re-executes
- **AND** if skip, marks task as skipped and continues
- **AND** if abort, stops the build and reports status

### Requirement: Loop back from verify to build
The system SHALL support looping back from verify stage to build stage if issues are found.

#### Scenario: Fix issues in verify stage
- **WHEN** verify stage finds issues
- **THEN** the system can transition back to build stage
- **AND** the agent generates new tasks to fix the issues
- **AND** appends them to prd.json
- **AND** continues the build loop
