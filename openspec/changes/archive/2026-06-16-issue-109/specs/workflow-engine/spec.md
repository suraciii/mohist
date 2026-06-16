## MODIFIED Requirements

### Requirement: Approval pending remains non-repairable

`user-approval` SHALL remain a read-only check over existing approval state and SHALL NOT become a repair target. The workflow engine SHALL treat approval pending as a local awaiting-approval outcome only after ordinary non-approval failures have been cleared. When the user requests changes (feedback), the engine SHALL schedule an `apply-feedback` task as normal workflow work rather than treating the request as a check failure or repair target.

#### Scenario: Pending approval pauses without repair

- **WHEN** `user-approval` returns `pending`
- **AND** no non-approval check in the effective phase result set is failing or errored
- **THEN** the workflow SHALL stop in awaiting approval
- **AND** it SHALL NOT run a fix task for `user-approval`

#### Scenario: Approval does not mask ordinary failures

- **WHEN** `user-approval` returns `pending`
- **AND** another non-approval check in the same phase result set returns `fail` or `error`
- **THEN** the workflow SHALL treat the phase as a repair-or-fail path rather than awaiting approval
- **AND** it SHALL NOT request or refresh approval until the ordinary failures are resolved

#### Scenario: Requested changes schedules feedback task not repair task

- **WHEN** the user requests changes at an approval gate
- **THEN** the workflow engine SHALL schedule an `apply-feedback` task as normal workflow work
- **AND** the engine SHALL NOT map the feedback request to a check repair task
- **AND** the engine SHALL NOT mark the stage as failed
- **AND** the feedback task SHALL execute before checks rerun and approval is re-requested

## ADDED Requirements

### Requirement: Workflow engine schedules apply-feedback as normal task

The workflow engine SHALL treat `apply-feedback` as an ordinary agent-session-backed workflow task. The task SHALL execute through the same shared task execution primitives used by other agent-session tasks. The engine SHALL NOT create a special feedback-only execution path.

#### Scenario: apply-feedback executes through AgentSessionTaskHandler

- **WHEN** the engine dispatches `apply-feedback`
- **THEN** it SHALL resolve the task through the configured task execution policy
- **AND** agent-session-backed execution SHALL use the same `AgentSessionTaskHandler` used by other agent tasks
- **AND** task result reporting SHALL use normal task result semantics

#### Scenario: Feedback task dispatch includes approvalFeedback context

- **WHEN** the engine dispatches `apply-feedback`
- **THEN** the dispatch context SHALL include the `approvalFeedback` object with id, stage, summary, and CLI command
- **AND** the prompt SHALL be rendered from the configured prompt source (built-in or custom)
- **AND** the prompt SHALL include the CLI read command for the full feedback body

### Requirement: Feedback loop reruns checks before re-approval

After the `apply-feedback` task completes successfully, the workflow engine SHALL rerun the configured stage checks before requesting approval again. The engine SHALL NOT request approval while feedback-driven changes remain unvalidated.

#### Scenario: Checks rerun after successful feedback task

- **WHEN** `apply-feedback` completes successfully
- **THEN** the engine SHALL invalidate prior check and approval evidence for the stage
- **AND** the engine SHALL rerun the stage checks in their configured order
- **AND** approval SHALL only be requested after all checks pass

#### Scenario: Failed check after feedback blocks re-approval

- **WHEN** checks rerun after `apply-feedback` completes
- **AND** one or more checks fail
- **THEN** the engine SHALL enter the normal check failure repair path
- **AND** approval SHALL NOT be requested until failures are resolved
- **AND** the feedback SHALL remain resolved regardless of subsequent check failures
