## MODIFIED Requirements

### Requirement: Workflow runners materialize or report required work before completion
Workflow runners SHALL materialize required work into StageRun evidence or record a recoverable blocked/failure reason before asking WorkflowRun to complete a stage.

#### Scenario: Explicit completion uses the domain guard
- **WHEN** a runner reaches the end of its local execution loop
- **THEN** it SHALL call the WorkflowRun completion decision used by `nextWork()`
- **AND** it SHALL surface the returned blocked reason instead of advancing the stage directly

#### Scenario: Build runner records dynamic source outcome
- **WHEN** Build evaluates `tasks.json`
- **THEN** the runner SHALL record whether the source was evaluated successfully, missing, invalid, or empty
- **AND** successful evaluation SHALL materialize generated tasks as StageRun TaskRun records before task execution or completion decisions

#### Scenario: Runtime work invalidates dependent checks
- **WHEN** a runner appends runtime work that can change the candidate or stage facts
- **THEN** it SHALL preserve reason or causedBy metadata on the appended TaskRun
- **AND** it SHALL invalidate or replace dependent checks and approval evidence according to policy before completion can continue

#### Scenario: Check and Integrate runners record authoritative evidence
- **WHEN** Check or Integrate succeeds
- **THEN** the runner SHALL persist the current task/check and delivery evidence required by WorkflowRun
- **AND** it SHALL NOT rely on AgentSession status or merge state alone to request Done
