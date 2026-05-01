## ADDED Requirements

### Requirement: Rerun re-executes current stage with orphan cleanup

The system SHALL provide a rerun operation that re-executes the current pipeline stage from scratch. Rerun SHALL clean up orphaned coder sessions (status `running` → `failed`), clear the current stage checkpoint, reset approval/blocked state, and resume the pipeline from the current stage without changing stage.

#### Scenario: Rerun an active issue stuck after agent crash
- **WHEN** issue is in `active` status at stage `build`
- **AND** no agent is currently running for this issue
- **AND** orphan coder sessions exist with status `running`
- **THEN** all orphan coder sessions for this issue are set to `failed`
- **AND** the current stage checkpoint is cleared
- **AND** `approval_state` is cleared, `blocked_reason` is cleared, `retry_count` is reset to 0
- **AND** issue status remains `active`, stage remains `build`
- **AND** `resumePipeline()` is called to re-execute the `build` stage

#### Scenario: Rerun a blocked issue
- **WHEN** issue is in `blocked` status at stage `plan`
- **AND** no agent is currently running for this issue
- **THEN** orphan sessions are cleaned to `failed`
- **AND** checkpoint is cleared, approval/blocked state is reset
- **AND** issue status is set to `active`, stage remains `plan`
- **AND** pipeline resumes from `plan` stage

#### Scenario: Rerun a closed issue
- **WHEN** issue is in `closed` status at stage `build`
- **AND** no agent is currently running for this issue
- **THEN** issue is reopened (status → `active`, stage stays `build`)
- **AND** orphan sessions are cleaned, checkpoint and state are reset
- **AND** pipeline resumes from `build` stage

#### Scenario: Rerun rejected when agent is running
- **WHEN** issue has an agent currently running
- **THEN** rerun is rejected with an error indicating agent is active

#### Scenario: Rerun preserves prior stage outputs
- **WHEN** rerun is executed on an issue at stage `build`
- **THEN** files already committed to the worktree are preserved
- **AND** plan artifacts from the `plan` stage are preserved
- **AND** only the current stage checkpoint is cleared (prior stage checkpoints unchanged)

### Requirement: Rerun ensures worktree exists

The rerun operation SHALL verify that a worktree exists for the issue before resuming the pipeline. If the worktree does not exist, it SHALL be created.

#### Scenario: Worktree exists
- **WHEN** rerun is called and the issue's worktree already exists
- **THEN** pipeline resumes without recreating the worktree

#### Scenario: Worktree missing
- **WHEN** rerun is called and the issue's worktree does not exist
- **THEN** the worktree is created before resuming the pipeline

### Requirement: Orphan coder session cleanup

The system SHALL clean up all coder sessions with status `running` for a given issue, setting them to `failed`. This operation SHALL be available as a reusable service method.

#### Scenario: Cleanup multiple orphan sessions
- **WHEN** an issue has 3 coder sessions with status `running` and no active agent
- **THEN** all 3 sessions are updated to status `failed`

#### Scenario: No orphan sessions to clean
- **WHEN** an issue has no coder sessions with status `running`
- **THEN** cleanup is a no-op and rerun proceeds normally
