## MODIFIED Requirements

### Requirement: Task status persistence

The system SHALL persist task execution status in tasks.json for recovery.

**File:** `{change-path}/tasks.json`

```json
{
  "version": 1,
  "tasks": [
    {"id": "T-001", "order": 1, "title": "...", "passes": true, "attempts": 1, "error": null},
    {"id": "T-002", "order": 2, "title": "...", "passes": true, "attempts": 1, "error": null},
    {"id": "T-003", "order": 3, "title": "...", "passes": false, "attempts": 3, "error": "Missing backend validation"}
  ]
}
```

When all tasks have `passes=true` upon loop entry, the system SHALL treat this as corrupted state only if no checkpoint recovery is in progress (i.e., `skipTaskIds` is empty). During checkpoint recovery, all-pass is the expected and valid state.

#### Scenario: Resume from failed task
- **WHEN** user runs `mo issue resume` after build failure
- **THEN** main-agent reads tasks.json
- **AND** identifies the first task with passes=false
- **AND** loads learnings from T-001 and T-002
- **AND** continues execution from T-003

#### Scenario: All tasks pre-passed without checkpoint (corrupted)
- **WHEN** all tasks in tasks.json have passes=true
- **AND** no skipTaskIds are provided (not a checkpoint recovery)
- **THEN** the system SHALL reset all tasks to passes=false
- **AND** write the updated tasks.json
- **AND** proceed to execute tasks from the beginning

#### Scenario: All tasks pre-passed with checkpoint recovery
- **WHEN** all tasks in tasks.json have passes=true
- **AND** skipTaskIds covers all task IDs (checkpoint recovery in progress)
- **THEN** the system SHALL NOT reset any task
- **AND** return a successful result immediately with completed equal to total
