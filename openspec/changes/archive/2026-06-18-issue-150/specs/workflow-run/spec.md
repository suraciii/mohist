## MODIFIED Requirements

### Requirement: Task completion persists clean-worktree verification evidence

WorkflowRun SHALL require clean-worktree verification evidence AND branch-stability verification evidence before marking any task as completed. A task result that lacks clean-worktree evidence, or whose workspace is not on the expected `workspace.branch`, SHALL be treated as incomplete, and the task SHALL NOT transition to a terminal completed state. Branch-stability evidence SHALL record that the workspace was on `workspace.branch` at the task end boundary, complementing the existing clean-worktree evidence.

#### Scenario: Clean worktree is recorded in task completion evidence

- **WHEN** a task result is reported to WorkflowRun
- **AND** the result includes a successful completion status
- **THEN** the task result SHALL include clean-worktree verification evidence indicating that `git status --porcelain` returned empty output in the task workspace

#### Scenario: Task without clean-worktree evidence cannot complete

- **WHEN** a task result is reported to WorkflowRun with successful completion status
- **AND** the result does not include clean-worktree verification evidence
- **THEN** the runner MUST NOT have reported the task as completed
- **AND** the WorkflowRun SHALL treat the task as incomplete
- **AND** the task SHALL NOT be considered for stage progression

The runner-side guarantee is the enforcement point in this change. Server-side WorkflowRun validation of the clean-worktree evidence is a separate concern and is out of scope for this issue.

#### Scenario: Dirty-worktree task failure is visible in WorkflowRun

- **WHEN** a task fails with structured dirty-worktree evidence
- **THEN** the WorkflowRun task result SHALL include the structured dirty-worktree evidence
- **AND** the failure SHALL be visible in issue detail and CLI surfaces as a task failure with the dirty-worktree reason

#### Scenario: Task completion records branch-stability evidence

- **WHEN** a task result is reported to WorkflowRun with a successful completion status
- **THEN** the task result SHALL include branch-stability verification evidence indicating that the task workspace was on `workspace.branch` at the task end boundary
- **AND** the evidence SHALL be recorded alongside the clean-worktree verification evidence

#### Scenario: Task ending off the run branch cannot complete

- **WHEN** a task result is reported to WorkflowRun with a successful completion status
- **AND** the task workspace was on a branch other than `workspace.branch` at the task end boundary
- **THEN** the runner MUST NOT have reported the task as completed
- **AND** the WorkflowRun SHALL treat the task as incomplete
- **AND** the failure SHALL be surfaced as a branch-invariant violation rather than a generic task failure
