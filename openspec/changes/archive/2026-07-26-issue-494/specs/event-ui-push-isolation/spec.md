### Requirement: Push delivery is isolated from durable event delivery

The Server SHALL deliver Web event pushes and runner lifecycle pushes as best-effort notifications outside the durable domain-event dispatch loop. Notification delivery, connection lookup, and notification timeout failures MUST be logged without retrying through durable dispatch, blocking events from the same source, or creating durable-dispatch dead letters.

#### Scenario: A Web event push fails
- **WHEN** a domain event has target Web connections and sending the event notification to one or more connections fails
- **THEN** the Server logs the failed notification and the durable delivery of the domain event remains settled without a retry or dead letter caused by that push failure

#### Scenario: Runner terminal-status push cannot be delivered
- **WHEN** a terminal workflow status notification cannot be delivered because the assigned runner is disconnected or the SignalR send fails or times out
- **THEN** the Server logs or drops the notification as a best-effort outcome and does not delay, retry, or dead-letter the source workflow event

### Requirement: Terminal workflow status notifications retain their lifecycle meaning

The Server SHALL attempt to notify the assigned connected runner when a workflow run becomes `Completed` or `Stopped`, using the existing workflow-run status notification payload. The Server MUST NOT send this terminal notification for `Failed`, because a failed workflow run remains recoverable and its workspace must remain available for subsequent retry or rerun work.

#### Scenario: A workflow run completes with a connected assigned runner
- **WHEN** a workflow run becomes `Completed` and its assigned runner has an active connection
- **THEN** the Server attempts a `ReceiveWorkflowRunStatus` notification containing that workflow run id and `Completed`

#### Scenario: A workflow run fails
- **WHEN** a workflow run becomes `Failed`
- **THEN** the Server does not send a terminal workflow-status notification or make the runner workspace cleanup-eligible

### Requirement: Missed runner push converges through authoritative status

The runner SHALL reconcile workflow-run status with the Server's authoritative workflow state through its existing status polling path. A missed or failed terminal status push MUST NOT leave a runner permanently unable to learn that its workspace is cleanup-eligible.

#### Scenario: A runner reconnects after missing a terminal push
- **WHEN** a runner was disconnected when one of its workflow runs became `Completed` or `Stopped` and later reconciles workflow-run status with the Server
- **THEN** the runner receives the authoritative terminal status and can make the corresponding workspace cleanup-eligible
