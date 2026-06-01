## ADDED Requirements

### Requirement: TaskRun preserves expected file metadata
WorkflowRun SHALL preserve task-declared expected file metadata on TaskRun state without storing file content. Expected file metadata SHALL include the file path, source `task-expect`, marker requirements when declared, and enough status facts for projections to report whether the file is expected and content can be fetched on demand.

#### Scenario: Task expectation files become TaskRun metadata
- **WHEN** a workflow task definition contains `with.expect.files`
- **THEN** the materialized TaskRun SHALL include required file metadata for each declared file
- **AND** each required file entry SHALL preserve its path, source `task-expect`, and marker requirements when present
- **AND** the TaskRun SHALL NOT store the file content itself

#### Scenario: Expected files survive runtime task updates
- **WHEN** task execution updates TaskRun status, attempts, output, artifacts, or failure evidence
- **THEN** the required file metadata declared by the task expectation SHALL remain available for workflow projections
- **AND** content availability MAY be recomputed from scoped workspace state rather than persisted as file content

### Requirement: TaskRun classifies user-facing and orchestration work
WorkflowRun SHALL classify TaskRun work so projections can distinguish user-facing tasks from orchestration or internal workflow tasks. User-facing progress summaries SHALL count only tasks classified as user-facing work.

#### Scenario: Orchestration work is marked separately
- **WHEN** a task is materialized from default stage definitions, runtime-added orchestration, repair, retry, rebase, or dynamic Build sources
- **THEN** the TaskRun SHALL expose a task classification or visibility value that identifies whether it is user-facing work or orchestration/internal work
- **AND** orchestration/internal tasks SHALL NOT be counted as user task completion in user-facing progress summaries

#### Scenario: Failed tasks do not inflate completion
- **WHEN** a current-stage task is failed
- **THEN** WorkflowRun-derived progress SHALL NOT count that task as completed
- **AND** a failed task MAY count as completed only when a later successful retry or replacement supersedes the failed attempt for the same user-facing work
