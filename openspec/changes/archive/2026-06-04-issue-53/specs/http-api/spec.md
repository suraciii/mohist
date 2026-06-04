## ADDED Requirements

### Requirement: Issue session metadata endpoint is lightweight
The HTTP API SHALL expose an issue-scoped agent session metadata endpoint at `GET /api/issues/:number/sessions/:name`. The response MUST include session identifiers, status, model, stage, title, timestamps, and aggregate metadata counts, and it SHALL NOT include raw session events, projected turns, assistant parts, or workflow log entries.

#### Scenario: Fetch session metadata only
- **WHEN** a client requests `GET /api/issues/51/sessions/T-003.1`
- **THEN** the response includes `id`, `sessionName`, `acpSessionId`, `status`, `model`, `stage`, `title`, `createdAt`, `completedAt`, and `metadata`
- **AND** `metadata` includes aggregate counts such as event count and tool count when available
- **AND** the response does not include `events`, `turns`, `assistant`, or `workflowLogs`

#### Scenario: Metadata endpoint stays lightweight
- **WHEN** a session has more than one thousand persisted events
- **THEN** `GET /api/issues/:number/sessions/:name` returns a metadata-only payload suitable for initial page load
- **AND** the server does not load or project the raw event stream to construct transcript turns

### Requirement: Issue session events endpoint returns raw ordered events
The HTTP API SHALL expose raw persisted agent session events at `GET /api/issues/:number/sessions/:name/events`. The response MUST be `{ events: SessionEvent[] }`, where each event includes `id`, `sequence`, `type`, raw `payload`, and `createdAt`. Events SHALL be ordered by ascending `sequence`.

#### Scenario: Fetch raw session events
- **WHEN** a client requests `GET /api/issues/51/sessions/T-003.1/events`
- **THEN** the response contains every persisted event for session `T-003.1`
- **AND** each event includes `id`, `sequence`, `type`, `payload`, and `createdAt`
- **AND** `payload` is returned as the raw stored payload value without server-side narrowing or projection

#### Scenario: Session events preserve sequence order
- **WHEN** persisted session events have sequences `3`, `1`, and `2` in storage query input order
- **THEN** `GET /api/issues/:number/sessions/:name/events` returns them in sequence order `1`, `2`, `3`

### Requirement: Issue workflow log endpoint returns raw workflow entries
The HTTP API SHALL expose workflow-level log entries at `GET /api/issues/:number/workflow-log`. The response MUST be `{ entries: WorkflowLogEntry[] }`, where entries represent raw `WorkflowEvents` rows for the issue ordered by ascending `createdAt`.

#### Scenario: Fetch raw workflow log
- **WHEN** a client requests `GET /api/issues/51/workflow-log`
- **THEN** the response contains workflow-level events for issue 51 from the `WorkflowEvents` store
- **AND** entries are ordered by `createdAt`
- **AND** each entry preserves its raw payload without session transcript projection

## REMOVED Requirements

### Requirement: Server-projected workflow session transcript endpoint
The HTTP API SHALL NOT expose a workflow session transcript response that combines session metadata, projected assistant turns, and raw agent session events in one payload.

**Reason**: The combined transcript DTO conflated metadata, raw agent session events, and projected chat view data, causing oversized initial loads and duplicated projection logic.

**Migration**: Clients MUST fetch session metadata from `GET /api/issues/:number/sessions/:name`, raw session events from `GET /api/issues/:number/sessions/:name/events`, and construct display views through the client session event projection module.

#### Scenario: Old transcript shape is not returned
- **WHEN** a client requests the replacement session metadata endpoint
- **THEN** the response does not include server-projected `turns`
- **AND** it does not include a `workflowLogs` field containing raw agent session events
