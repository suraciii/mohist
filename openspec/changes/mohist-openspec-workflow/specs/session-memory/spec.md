## ADDED Requirements

### Requirement: Session memory storage
The system SHALL store task execution learnings in structured session memory files.

#### Scenario: Store task learning
- **WHEN** a task completes (success or failure)
- **THEN** the system extracts key insights from the execution
- **AND** stores in `.mohist/issues/{id}/session-memories/{task-id}.json`
- **AND** the file contains: task_id, change_name, executed_at, success, insights[], adjustments[]

### Requirement: Session memory retrieval
The system SHALL retrieve and include relevant session memories when executing subsequent tasks.

#### Scenario: Load memories for task context
- **WHEN** assembling context for task execution
- **THEN** the system reads all session-memories/*.json for the current issue
- **AND** filters for memories from tasks with lower priority
- **AND** includes relevant insights in the task prompt

### Requirement: Memory-driven prompt adjustment
The system SHALL use session memories to adjust instructions for subsequent tasks.

#### Scenario: Adjust task based on previous learning
- **WHEN** a previous task discovered a constraint (e.g., "API schema needs adjustment")
- **THEN** the next task's prompt includes:
  - "Note: Task T-001 discovered that the API schema needs to support refresh tokens"
  - "Please update the type definitions accordingly"
- **AND** the agent adapts its approach based on this context

### Requirement: Memory lifecycle management
The system SHALL manage the lifecycle of session memories across issue stages.

#### Scenario: Clean up old memories
- **WHEN** an issue reaches done status
- **THEN** the system archives session memories with the change
- **AND** optionally cleans up based on retention policy
- **AND** memories remain accessible for future reference to the same issue
