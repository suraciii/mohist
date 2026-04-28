## MODIFIED Requirements

### Requirement: Task status persistence
The system SHALL persist task execution status in tasks.json for recovery. After writing tasks.json, the system SHALL commit the file in the worktree to ensure the update survives mergeBack.

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

#### Scenario: Resume from failed task
- **WHEN** user runs `mo issue resume` after build failure
- **THEN** main-agent reads tasks.json
- **AND** identifies the first task with passes=false
- **AND** loads learnings from T-001 and T-002
- **AND** continues execution from T-003

#### Scenario: tasks.json update is committed to worktree
- **WHEN** ralph updates a task's status in tasks.json (passes, attempts, error)
- **THEN** the system SHALL run `git add` and `git commit` for tasks.json in the worktree
- **AND** the commit message SHALL reference the task ID and status
- **AND** this ensures tasks.json changes are not lost during mergeBack
