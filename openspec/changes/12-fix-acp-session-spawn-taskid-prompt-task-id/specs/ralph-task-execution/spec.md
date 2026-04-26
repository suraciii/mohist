## MODIFIED Requirements

### Requirement: Ralph-style task loop execution
The system SHALL execute tasks from tasks.json in a loop, one at a time, until all are complete. Each task execution SHALL pass the task ID to the ACP session runner for correct log correlation.

**Loop Driver:** Mohist Main-agent (not a single long-running coder process)

#### Scenario: Execute pending tasks sequentially
- **WHEN** the build stage starts
- **THEN** the main-agent reads tasks.json
- **AND** identifies pending tasks (passes: false)
- **AND** selects the task with lowest order/priority
- **AND** assembles complete context (proposal + design + spec + learnings)
- **AND** calls `spawn_coder` with the assembled prompt **AND** `taskId` set to the task's `id` field
- **AND** waits for coder to complete
- **AND** verifies AC satisfaction
- **AND** updates passes/attempts/error in tasks.json
- **AND** repeats until all tasks are complete

#### Scenario: Task attempt log correlation
- **WHEN** a task attempt starts with task ID `T-001`
- **THEN** the ACP session spawn log SHALL contain `taskId: "T-001"`
- **AND** the workflow_log `task_started` event and ACP session spawn event SHALL share the same task ID
