## ADDED Requirements

### Requirement: Full-checkpoint short-circuit in RalphExecutor

When `skipTaskIds` covers all tasks in tasks.json, the system SHALL skip the main execution loop entirely and return a successful `RalphLoopResult` with the correct `completed` count.

#### Scenario: All tasks recovered from checkpoint
- **WHEN** `runRalphLoop` is called with `skipTaskIds` containing every task ID in tasks.json
- **THEN** the system SHALL set `passes=true` on each task as usual
- **AND** write the updated tasks.json
- **AND** return immediately with a `RalphLoopResult` where `completed` equals the total number of tasks, `failed` equals 0, `skipped` equals 0, `success` equals true
- **AND** emit a log entry indicating `recovered-from-checkpoint` with the task count
- **AND** NOT enter the main execution while-loop

#### Scenario: Partial checkpoint recovery proceeds normally
- **WHEN** `skipTaskIds` covers some but not all tasks
- **THEN** the system SHALL mark skipped tasks as passed and execute only the remaining pending tasks through the main loop
- **AND** the `completed` counter SHALL reflect only tasks actually executed in this run

### Requirement: allTasksPassed guard respects checkpoint recovery

The system SHALL NOT reset all tasks to `passes=false` when all tasks have `passes=true` AND `skipTaskIds` is non-empty. The all-pass state is expected during checkpoint recovery and MUST be preserved.

#### Scenario: allTasksPassed during checkpoint recovery
- **WHEN** all tasks have `passes=true` AND `skipTaskIds` is non-empty
- **THEN** the system SHALL NOT reset any task to `passes=false`
- **AND** the system SHALL NOT write to tasks.json for the reset
- **AND** the system SHALL proceed to the short-circuit return

#### Scenario: allTasksPassed without checkpoint recovery (corrupted state)
- **WHEN** all tasks have `passes=true` AND `skipTaskIds` is empty or undefined
- **THEN** the system SHALL reset all tasks to `passes=false`
- **AND** write the updated tasks.json
- **AND** proceed to execute tasks through the main loop

### Requirement: Checkpoint consistency cleanup in workflow-controller

After reading the build checkpoint and before executing the RalphExecutor, the system SHALL verify that checkpoint task IDs are consistent with the current tasks.json. If every task ID in the checkpoint already has `passes=true` in tasks.json, the system SHALL delete the redundant checkpoint.

#### Scenario: Checkpoint fully consistent with tasks.json
- **WHEN** the build checkpoint contains task IDs `[T-001, T-002, T-003]`
- **AND** all three tasks have `passes=true` in tasks.json
- **THEN** the system SHALL delete the build checkpoint before executing
- **AND** pass all task IDs as `skipTaskIds` to the executor

#### Scenario: Checkpoint partially consistent with tasks.json
- **WHEN** the build checkpoint contains task IDs `[T-001, T-002]`
- **AND** tasks.json shows T-001 `passes=true` and T-002 `passes=false`
- **THEN** the system SHALL keep the checkpoint
- **AND** pass only T-001 as `skipTaskIds` (the verified-passed task)
