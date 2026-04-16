## ADDED Requirements

### Requirement: Session memory storage
The system SHALL store task execution learnings in structured session memory files.

**Storage Location:**
```
openspec/changes/{change-name}/session-memories/{task-id}.json
```

#### Scenario: Store task learning
- **WHEN** a task completes (success or failure)
- **THEN** the Mohist Agent extracts key insights from the execution
- **AND** stores in `{change-path}/session-memories/{task-id}.json`
- **AND** the file contains:
  - task_id: string
  - timestamp: ISO8601
  - insights: string[] (discovered constraints/patterns)
  - adjustments: string[] (suggestions for subsequent tasks)
  - success: boolean
  - execution_summary: string

#### Scenario: Store failure learning
- **WHEN** a task fails after retries
- **THEN** the system stores:
  - failure_reason: string (why it failed)
  - failed_attempts: number
  - insights: ["The auth module uses a non-standard export pattern"]
  - adjustments: ["Look for exports in src/auth/index.ts instead of src/auth.ts"]

### Requirement: Session memory retrieval
The system SHALL retrieve and include relevant session memories when executing subsequent tasks.

#### Scenario: Load memories for task context
- **WHEN** assembling context for task T-003
- **THEN** the system reads all `session-memories/*.json` for the current Change
- **AND** includes insights from T-001 and T-002 in the prompt
- **AND** formats them as:
  ```
  [Previous Task Learnings]
  From T-001: "Project uses single quotes for strings"
  From T-002: "Tests require docker-compose to be running"
  ```

### Requirement: Memory-driven prompt adjustment
The system SHALL use session memories to adjust instructions for subsequent tasks.

#### Scenario: Adjust task based on previous learning
- **WHEN** a previous task discovered a constraint
- **THEN** the next task's prompt includes the constraint and adjustment
- **AND** the agent adapts its approach based on this context

### Requirement: Failure context for retry
The system SHALL include failure context when retrying a failed task.

#### Scenario: Retry with failure context
- **WHEN** task T-003 fails and needs retry
- **THEN** the retry prompt includes:
  ```
  [Previous Attempt Failed]
  Failure reason: Missing backend email validation
  
  [Adjustments for this attempt]
  - Implement validation in both frontend and backend
  - Check src/validators/email.ts for existing validation logic
  ```

### Requirement: Memory lifecycle management
The system SHALL manage the lifecycle of session memories across issue stages.

#### Scenario: Archive memories with change
- **WHEN** a Change is archived to `openspec/changes/archive/`
- **THEN** the session-memories directory is preserved in the archive
- **AND** memories remain accessible for future reference

#### Scenario: No automatic cleanup
- **WHEN** a Change is completed
- **THEN** session memories are NOT deleted
- **AND** they remain in the archive for historical analysis
