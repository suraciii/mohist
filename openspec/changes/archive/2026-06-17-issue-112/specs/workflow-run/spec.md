## MODIFIED Requirements

### Requirement: REQ-WR-005 Integrate runtime work is first-class WorkflowRun state

Integrate stage progress SHALL be represented in WorkflowRun using standard task and check entities. The Integrate StageRun SHALL expose ordered tasks `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge`, plus check `health:integrate`; merge delivery metadata and post-merge freeze state SHALL be persisted as WorkflowRun facts.

#### Scenario: Integrate stage is seeded with visible work

- **WHEN** an issue starts or resumes with an active WorkflowRun
- **THEN** the Integrate StageRun SHALL contain pending tasks `integrate:spec-sync`, `integrate:archive-change`, and `integrate:merge` in execution order
- **AND** it SHALL contain a pending check `health:integrate`

#### Scenario: Integrate merge records delivery facts

- **WHEN** `integrate:merge` completes successfully
- **THEN** the task result SHALL record `targetBranch`, `baseSha`, `candidateHeadSha`, `landedSha`, `pushRemote`, `remoteRefAfterPush`, `pushAttempts`, and `rebased` when available
- **AND** the Integrate StageRun SHALL record a freeze point that prevents later automatic code-modifying tasks

#### Scenario: Post-merge health failure is non-repairable

- **WHEN** `health:integrate` fails after `integrate:merge` has completed
- **THEN** WorkflowRun SHALL fail with reason `post-merge-health-failed`
- **AND** it SHALL NOT schedule `fix-integrate-health` regardless of check failure policy configuration

## ADDED Requirements

### Requirement: Task completion persists clean-worktree verification evidence

WorkflowRun SHALL require clean-worktree verification evidence before marking any task as completed. A task result that lacks clean-worktree evidence SHALL be treated as incomplete, and the task SHALL NOT transition to a terminal completed state.

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
