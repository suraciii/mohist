## ADDED Requirements

### Requirement: Coder sessions support attempt interruption evidence

Coder session tracking SHALL preserve the session evidence needed to connect stopped, cancelled, failed, or lost agent execution to the related workflow work item attempt without making runtime proof a first-class persisted domain entity.

#### Scenario: Running session records attempt evidence identifiers

- **WHEN** a workflow work item starts an agent-backed attempt
- **THEN** the related coder session or attempt metadata SHALL retain identifiers such as issue id, ACP session id, execution id, queue task id, or process id when available
- **AND** those identifiers MAY be used by reconciliation to evaluate liveness

#### Scenario: Stopped session records cancellation evidence

- **WHEN** Mohist intentionally stops a coder session that is executing a workflow work item
- **THEN** coder session tracking SHALL record cancelled or interrupted session state and diagnostic reason
- **AND** the workflow attempt interruption path SHALL be able to associate that evidence with the current work item

#### Scenario: Lost session remains inspection evidence

- **WHEN** a coder session is stale or its process disappears
- **THEN** coder session tracking SHALL preserve historical session evidence for inspection
- **AND** reconciliation MAY use the absence of live matching evidence to interrupt the latest running attempt
- **AND** historical evidence SHALL NOT by itself make interrupted work a failed retry target
