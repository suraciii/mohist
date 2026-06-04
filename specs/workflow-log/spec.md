## ADDED Requirements

### Requirement: Workflow events are exposed as a first-class issue log
Workflow-level events SHALL be available through a dedicated issue workflow log API that reads the existing `WorkflowEvents` store. These events MUST represent workflow activity such as stage, task, check, approval, retry, and workflow start events, and SHALL NOT be conflated with agent session stream events.

#### Scenario: Workflow log contains workflow-level events
- **WHEN** a client requests `GET /api/issues/:number/workflow-log`
- **THEN** the response includes entries from the issue's `WorkflowEvents` rows
- **AND** entries describe workflow-level activity rather than agent message chunks or tool-call stream fragments

#### Scenario: Workflow log preserves raw payloads
- **WHEN** workflow event payloads contain stage, task, check, approval, retry, or started data
- **THEN** the API returns those payloads without transforming them into session transcript turns or timeline rounds

#### Scenario: Workflow log ordering is chronological
- **WHEN** an issue has multiple persisted workflow events
- **THEN** `GET /api/issues/:number/workflow-log` returns entries ordered by ascending `createdAt`

### Requirement: Workflow log is separate from session events
Workflow log access SHALL remain separate from agent session event access. The workflow log endpoint MUST return workflow events only, and the session events endpoint MUST return agent session events only.

#### Scenario: Session events are not returned as workflow log entries
- **WHEN** a session has raw `agent_message_chunk` and `tool_call_update` events
- **THEN** `GET /api/issues/:number/workflow-log` does not expose those session stream events as workflow log entries
- **AND** those events are available from `GET /api/issues/:number/sessions/:name/events`
