## MODIFIED Requirements

### Requirement: Non-Build tasks execute through a minimal shared handler contract

Non-Build task execution SHALL support runtime-added rebase work through the same shared handler contract used by other WorkflowRun tasks. `rebase-branch` SHALL execute as ordinary WorkflowRun task work and SHALL NOT use a queue-only rebase execution path as the primary workflow behavior.

#### Scenario: Rebase task executes through normal workflow scheduling

- **WHEN** `WorkflowRun.nextWork()` returns `task: rebase-branch`
- **THEN** the workflow engine and stage runner SHALL execute that task through the shared task runtime
- **AND** later tasks or checks SHALL NOT run until `rebase-branch` reaches a terminal state

### Requirement: merge-ready invalidates review on code change

The workflow engine SHALL invalidate review, check, and approval state based on actual candidate snapshot change facts rather than on rebase intent alone. When a completed `rebase-branch` task reports `shaChanged=true`, the affected stage policy SHALL reset the dependent review/check state; when `shaChanged=false`, the prior review/check state MAY remain valid.

#### Scenario: Rebase with unchanged snapshot preserves review state

- **WHEN** `rebase-branch` completes successfully
- **AND** its result reports `shaChanged=false`
- **THEN** existing review/check state SHALL remain valid
- **AND** the workflow SHALL continue without forcing re-review solely because the user clicked Rebase

#### Scenario: Rebase with changed snapshot invalidates check-stage review truth

- **WHEN** `rebase-branch` completes successfully in Check stage
- **AND** its result reports `shaChanged=true`
- **THEN** the workflow SHALL invalidate `ai-review`, `review-passed`, `merge-ready`, and approval state for that stage
- **AND** later work SHALL re-run against the new snapshot before approval can be requested again

#### Scenario: Failed rebase blocks later work

- **WHEN** `rebase-branch` fails
- **THEN** the current stage SHALL fail through normal task failure semantics
- **AND** later tasks or checks SHALL NOT execute
