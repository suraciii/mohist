## MODIFIED Requirements

### Requirement: Ralph-style task loop execution
The system SHALL execute tasks from tasks.json in a loop, one at a time, until all are complete.

**Loop Driver:** Mohist Main-agent (not a single long-running coder process)

#### Scenario: Execute pending tasks sequentially
- **WHEN** the build stage starts
- **THEN** the main-agent reads tasks.json
- **AND** validates the dependency graph (see task-dependency-validation spec)
- **AND** identifies pending tasks (passes: false) whose `dependsOn` tasks have all passed
- **AND** selects the ready task with the lowest `order` value
- **AND** assembles complete context (proposal + design + spec + learnings)
- **AND** calls `spawn_coder` with the assembled prompt
- **AND** waits for coder to complete
- **AND** verifies AC satisfaction
- **AND** updates passes/attempts/error in tasks.json
- **AND** repeats until all tasks are complete

#### Scenario: Task blocked by unmet dependency
- **WHEN** the next task by `order` has `dependsOn: ["T-003"]` and T-003 has `passes: false`
- **THEN** the system SHALL skip that task and look for the next ready task
- **AND** if no tasks are ready (all pending tasks have unmet dependencies), the system SHALL pause and report a deadlock

#### Scenario: Dependency-aware task selection with multiple ready tasks
- **WHEN** tasks T-001 (passed), T-002 (pending, dependsOn: ["T-001"]), T-003 (pending, dependsOn: ["T-001"]) are loaded
- **THEN** both T-002 and T-003 are ready (T-001 has passed)
- **AND** the system selects the one with the lower `order` value
