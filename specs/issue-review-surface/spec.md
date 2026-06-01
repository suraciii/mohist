## ADDED Requirements

### Requirement: Issue Detail supports task artifact inspection
Issue Detail SHALL provide a task artifact inspection affordance for files required by workflow task expectations. This affordance SHALL support reviewing in-progress required artifacts from task rows and SHALL remain separate from the dedicated Files changed review surface.

#### Scenario: Inspect expected artifact from task
- **WHEN** a workflow task declares an expected artifact file such as `proposal.md`, `design.md`, `tasks.json`, `self-review.md`, or `review.md`
- **THEN** Issue Detail SHALL allow the user to open that required file from the task context
- **AND** the displayed content SHALL reflect the current scoped worktree content loaded on demand

#### Scenario: Task artifact review stays distinct from Files changed
- **WHEN** a user inspects a task-required artifact on Issue Detail
- **THEN** the surface SHALL present it as task artifact content rather than the primary changed-file review experience
- **AND** the dedicated Files changed page SHALL remain the primary surface for reviewing final code changes and diffs

#### Scenario: No permanent artifact content storage
- **WHEN** a user opens or closes a task-required artifact viewer
- **THEN** Mohist SHALL NOT persist the file content into the workflow domain model
- **AND** later views SHALL load current content through the scoped file-content path when requested
