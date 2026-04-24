## ADDED Requirements

### Requirement: Build completion SHALL correctly count failed tasks after auto-skip resolution
The ralph executor SHALL delay incrementing the `failed` counter until after the auto-skip decision is made. Only tasks that are neither auto-skipped nor passing SHALL be counted as failed.

#### Scenario: Task auto-skipped after failure
- **WHEN** a task fails and the system decides to auto-skip it (no onAskUser handler available)
- **THEN** the task SHALL be marked as `passes=true` with status `skipped` in taskResults, and the `failed` counter SHALL NOT be incremented

#### Scenario: Task genuinely fails without auto-skip
- **WHEN** a task fails and auto-skip does not apply
- **THEN** the `failed` counter SHALL be incremented and the task SHALL have status `failed` in taskResults

#### Scenario: All tasks pass or are auto-skipped
- **WHEN** all tasks in a build stage either pass or are auto-skipped
- **THEN** `result.success` SHALL be `true` and the pipeline SHALL report build success

### Requirement: Build result SHALL reflect final task state
The build completion result SHALL be based on the final state of tasks in tasks.json, not on intermediate counter values that may be stale.

#### Scenario: Tasks updated to passes=true after intermediate failure
- **WHEN** a task is temporarily marked as failed but later updated to passes=true (via auto-skip or retry)
- **THEN** the final result SHALL reflect the updated passes=true state
