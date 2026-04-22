## ADDED Requirements

### Requirement: Task schema default value filling
`readTasks()` SHALL normalize each task after parsing by filling missing fields with defaults:
- `attempts`: default `0`
- `passes`: default `false`
- `order`: default `999999`
- `error`: default `null`

#### Scenario: tasks.json without attempts field
- **WHEN** agent generates tasks.json with tasks that have no `attempts` field
- **THEN** `readTasks()` returns tasks with `attempts: 0` filled in
- **AND** Ralph loop for-loop condition evaluates to `1 <= 3` (not `NaN <= NaN`)
- **AND** task execution proceeds normally

#### Scenario: tasks.json with all fields present
- **WHEN** tasks.json has explicit `attempts`, `passes`, `order` fields
- **THEN** `readTasks()` preserves the original values without overriding

### Requirement: Task skip marks passes as false
When Ralph loop detects that a task was skipped without any attempt being made (for-loop body never executed), the task SHALL be marked as `passes: false` with an error message explaining the skip reason.

#### Scenario: Task skipped due to NaN loop condition
- **WHEN** a task has `attempts: undefined` causing the for-loop to not execute
- **THEN** `updateTaskInList` sets `passes: false` (not `passes: true`)
- **AND** error message explains the reason

#### Scenario: Task executed at least once
- **WHEN** the for-loop executes at least one attempt
- **THEN** existing success/failure logic applies unchanged
