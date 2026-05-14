## MODIFIED Requirements

### Requirement: REQ-WUI-WORKFLOW-RUN-001 Issue Detail renders WorkflowRun-backed progress

Issue Detail SHALL render user-triggered rebase as ordinary WorkflowRun task progress in the current stage task list. Rebase-specific SSE or toast feedback MAY remain as supplementary detail, but users SHALL be able to understand rebase status from the same canonical task list used for other workflow work.

#### Scenario: Rebase becomes visible task state after click

- **WHEN** a user triggers rebase for the current issue
- **THEN** Issue Detail SHALL show `Rebase branch` in the current stage task list using canonical stage-state or WorkflowRun-backed data
- **AND** the task SHALL transition through pending, running, completed, or failed like other visible tasks

#### Scenario: Rebase visibility does not rely on bespoke SSE interpretation

- **WHEN** rebase work has been scheduled in the WorkflowRun
- **THEN** Issue Detail SHALL NOT require dedicated rebase-only SSE semantics to know that rebase is part of the workflow
- **AND** any retained rebase progress or conflict messaging SHALL be secondary to canonical task-list state
