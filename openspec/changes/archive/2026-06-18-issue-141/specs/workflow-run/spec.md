## MODIFIED Requirements

### Requirement: REQ-WR-005 Integrate runtime work is first-class WorkflowRun state

Integrate stage progress SHALL be represented in WorkflowRun using standard task and check entities. The Integrate StageRun SHALL expose ordered tasks `integrate:spec-sync`, `integrate:archive-change`, `integrate:prepare`, and `integrate:publish`, plus check `health:integrate`; delivery metadata (prepared base, published commit, push ownership) and post-publish freeze state SHALL be persisted as WorkflowRun facts.

#### Scenario: Integrate stage is seeded with visible work

- **WHEN** an issue starts or resumes with an active WorkflowRun
- **THEN** the Integrate StageRun SHALL contain pending tasks `integrate:spec-sync`, `integrate:archive-change`, `integrate:prepare`, and `integrate:publish` in execution order
- **AND** it SHALL contain a pending check `health:integrate`

#### Scenario: Integrate prepare records reconciliation facts

- **WHEN** `integrate:prepare` completes successfully
- **THEN** the task result SHALL record `targetBranch`, the base commit it prepared against, the prepared candidate head, and `rebased` when available
- **AND** later Integrate work SHALL treat the issue branch as up to date with that base

#### Scenario: Integrate publish records delivery facts and freezes

- **WHEN** `integrate:publish` completes successfully
- **THEN** the task result SHALL record `targetBranch`, `baseSha`, the landed commit sha, and that the change was pushed to the remote
- **AND** the Integrate StageRun SHALL record a freeze point that prevents later automatic code-modifying tasks

#### Scenario: Post-publish health failure is non-repairable

- **WHEN** `health:integrate` fails after `integrate:publish` has completed
- **THEN** WorkflowRun SHALL fail with reason `post-publish-health-failed`
- **AND** it SHALL NOT schedule `fix-integrate-health` regardless of check failure policy configuration
