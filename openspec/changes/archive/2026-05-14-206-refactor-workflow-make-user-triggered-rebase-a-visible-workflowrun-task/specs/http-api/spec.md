## MODIFIED Requirements

### Requirement: API 提供操作接口

`POST /api/issues/:number/rebase` SHALL schedule visible workflow rebase work for non-Done stages through the active WorkflowRun instead of enqueueing a hidden issue task queue `rebase` job. The response SHALL communicate that workflow work was scheduled, and the current stage task list SHALL become the canonical source of progress.

#### Scenario: Non-Done rebase schedules WorkflowRun task

- **WHEN** a client calls `POST /api/issues/:number/rebase` for an issue in Plan, Build, Check, or Integrate
- **THEN** the API SHALL append or reuse `rebase-branch` in the current WorkflowRun stage
- **AND** it SHALL NOT use the hidden issue task queue `rebase` job as the primary execution path
- **AND** the response SHALL indicate that rebase work is now represented in workflow task state

#### Scenario: Duplicate rebase request is idempotent for in-flight work

- **WHEN** a client calls `POST /api/issues/:number/rebase`
- **AND** the current stage already has a `rebase-branch` task in `pending` or `running` state
- **THEN** the API SHALL return success without scheduling a duplicate task
- **AND** the existing workflow task SHALL remain the canonical progress record
