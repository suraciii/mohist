### Requirement: State holds only adjudication facts
WorkflowRun State SHALL hold only the minimal running facts required for adjudication: run and stage status, worker assignment, per-TaskRun state-machine fields, workspace and repository references, and each task's own output. State SHALL NOT store content that can be rebuilt or referenced elsewhere — including dispatch payloads, full prompt bodies, and aggregates of all prior task outputs.

#### Scenario: Persisted State contains adjudication fields only
- **WHEN** a run with multiple dispatched and completed tasks is persisted
- **THEN** the State SHALL contain each TaskRun's own output, status, and assignment
- **AND** the State SHALL NOT contain any `WorkDispatch`, full prompt body, or aggregate of all prior task outputs

### Requirement: TaskRun does not embed a dispatch payload
A TaskRun SHALL NOT embed a `WorkDispatch` or any dispatch payload. The TaskRun SHALL carry only the fields its own adjudication requires (definition reference, attempt, status, assignment, output, error, recovery state).

#### Scenario: Completed task carries output, not a dispatch payload
- **WHEN** a task is dispatched, runs, and reports Completed
- **THEN** the persisted TaskRun SHALL carry its output and terminal status
- **AND** the TaskRun SHALL NOT carry an embedded `WorkDispatch` or dispatch payload

#### Scenario: Failed task carries error, not a dispatch payload
- **WHEN** a task is dispatched, runs, and reports Failed
- **THEN** the persisted TaskRun SHALL carry its error and terminal status
- **AND** the TaskRun SHALL NOT carry an embedded `WorkDispatch` or dispatch payload

### Requirement: No cross-attempt duplicated full content
A single TaskRun SHALL NOT carry full-content copies that are duplicated across attempts (full prompt maps, aggregated prior-task outputs). Content that grows with task count or retry count SHALL be accounted per-entry: each TaskRun carries only its own adjudication fields, so serialized State size SHALL grow additively with the number of attempts, not multiplicatively.

#### Scenario: Multiple retries do not duplicate full content per attempt
- **WHEN** a stage contains several completed and failed task attempts for the same definition, each dispatched with full prompts and prior-task outputs
- **THEN** no TaskRun in the persisted State SHALL carry a copy of the full prompt set or the aggregate of all prior task outputs
- **AND** the serialized State size SHALL grow additively with the number of attempts, not multiplicatively

### Requirement: Active run State volume within budget
An active run's serialized State SHALL remain within the design budget: hundreds of KB in normal operation, and SHALL NOT exceed 1 MB. A State exceeding 1 MB SHALL be treated as a content-boundary violation, not accommodated by resizing the budget.

#### Scenario: Active run with multiple tasks and retries stays bounded
- **WHEN** an active run with multiple stages, several tasks, and multiple retries is persisted
- **THEN** the serialized State SHALL remain within hundreds of KB and SHALL NOT exceed 1 MB
