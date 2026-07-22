### Requirement: ApprovalFeedback exposed only as work.approvalFeedback
ApprovalFeedback facts SHALL be exposed in the template context exclusively under `work.approvalFeedback`, and only in the task that the feedback produced. The exposed fields SHALL be `id`, `stage`, `createdAt`, and `summary`. A task that is not produced by an ApprovalFeedback SHALL NOT carry a `work.approvalFeedback` object.

#### Scenario: A feedback task reads the feedback identity
- **WHEN** a task produced by an ApprovalFeedback references `${{ work.approvalFeedback.id }}`
- **THEN** the expression resolves to the id of the feedback that produced that task

#### Scenario: A feedback task reads the feedback summary
- **WHEN** a feedback task references `${{ work.approvalFeedback.summary }}`
- **THEN** the expression resolves to the summary of the triggering feedback

#### Scenario: A non-feedback task has no approvalFeedback
- **WHEN** a regular task references `${{ work.approvalFeedback.id }}`
- **THEN** the expression does not resolve and the task fails

#### Scenario: The bare approvalFeedback root is absent
- **WHEN** any task references `${{ approvalFeedback.id }}` (bare root)
- **THEN** the expression does not resolve and the task fails

### Requirement: No pre-rendered feedback command
The template context SHALL NOT provide a pre-rendered `command` field under `work.approvalFeedback` or any other root. A Prompt or task that needs the feedback-show invocation SHALL construct it from `work.approvalFeedback.id`, `issue.number`, and `issue.projectId`.

#### Scenario: The command field is absent
- **WHEN** the dispatch context is built for a feedback task
- **THEN** no `command` field exists under `work.approvalFeedback` or any bare `approvalFeedback` root

#### Scenario: A prompt builds the invocation from primitives
- **WHEN** a builtin Prompt needs the feedback-show command and the feedback task exposes `work.approvalFeedback.id` with `issue.number` and `issue.projectId`
- **THEN** the Prompt body constructs the command string from those fields rather than reading a pre-rendered `command`
