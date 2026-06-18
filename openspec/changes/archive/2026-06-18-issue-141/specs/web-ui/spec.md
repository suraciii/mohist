## MODIFIED Requirements

### Requirement: REQ-WUI-005 Integrate progress is visible in Issue Detail

Issue Detail progress surfaces SHALL render Integrate from persisted WorkflowRun task and check state so users can see which integration step is running, which steps completed, whether final verification passed or failed, and whether delivery has already happened. The delivery portion SHALL show `integrate:prepare` and `integrate:publish` as distinct tasks so conflict resolution is visible and recoverable.

#### Scenario: Integrate tasks are visible while running

- **WHEN** the active stage is Integrate and task state is available
- **THEN** Issue Detail SHALL display `integrate:spec-sync`, `integrate:archive-change`, `integrate:prepare`, and `integrate:publish` as separate tasks in order
- **AND** it SHALL show current running, completed, or failed status for each task

#### Scenario: Prepare records reconciliation facts

- **WHEN** `integrate:prepare` completes successfully
- **THEN** Issue Detail SHALL surface the base commit prepared against and the prepared candidate head as delivery metadata
- **AND** later Integrate work SHALL be treated as up to date with that base

#### Scenario: Publish records delivery facts

- **WHEN** `integrate:publish` completes successfully
- **THEN** Issue Detail SHALL surface the landed commit sha and that the change was pushed to the remote as delivery metadata
- **AND** it SHALL not require users to inspect logs to know that delivery occurred

#### Scenario: Delivery failure kind is rendered with next-action guidance

- **WHEN** `integrate:prepare` or `integrate:publish` fails
- **THEN** Issue Detail SHALL render the delivery failure kind (`conflict`, `base-moved`, or `retry-safe`)
- **AND** it SHALL surface the recommended next action implied by that kind
