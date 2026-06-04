## ADDED Requirements

### Requirement: Persisted session events are served raw for client projection
Pipeline session event access SHALL provide raw persisted `WorkflowAgentSessionEvents` rows to clients instead of server-projected transcript turns. The raw event contract MUST preserve event `type`, `sequence`, raw `payload`, and creation time so clients can derive chat, timeline, and compact views from the same event stream.

#### Scenario: Raw event payloads remain unprojected
- **WHEN** the server returns persisted `agent_message_chunk`, `tool_call`, `tool_call_update`, `agent_thought_chunk`, `mohist_prompt`, `available_commands_update`, `usage_update`, `agent_session_terminal`, or `agent_liveness_status` events
- **THEN** each event payload is returned as stored
- **AND** the server does not convert those events into assistant text, reasoning, or tool parts

#### Scenario: Event ordering is stable for projection
- **WHEN** a client loads a session event stream after a page refresh
- **THEN** the returned events are ordered by ascending `sequence`
- **AND** applying the client projection to those events produces the same visible transcript structure as live processing of the same stream

### Requirement: Server no longer owns assistant transcript projection
The pipeline session event pipeline SHALL NOT build assistant transcript parts or turn structures on the server for issue session detail responses. Assistant text, reasoning, tool parts, timeline rounds, and compact summaries MUST be derived in the web client from raw session events.

#### Scenario: BuildAssistantParts is removed from server behavior
- **WHEN** session detail data is loaded through the new metadata and events endpoints
- **THEN** server code does not call `BuildAssistantParts` or an equivalent assistant-part projection function
- **AND** no response contains `WorkflowAgentSessionTranscript.Turns`

#### Scenario: Raw stream supports future cursors
- **WHEN** a client receives session events
- **THEN** every event includes `sequence`
- **AND** the endpoint shape remains `{ events: [...] }` so cursor pagination can be added later without changing event item shape

## REMOVED Requirements

### Requirement: WorkflowAgentSessionTranscript turns are server-owned
The server SHALL NOT provide `WorkflowAgentSessionTranscript.Turns` as the canonical representation of a session transcript.

**Reason**: Server-owned turns duplicate client live-update projection and drift from timeline reconstruction behavior.

**Migration**: Clients MUST call the raw session events endpoint and project those events through `viewSessionEvents`.

#### Scenario: Transcript turns are absent from session APIs
- **WHEN** a client fetches session metadata or session events
- **THEN** neither response includes a `turns` collection
- **AND** assistant parts are not precomputed by the server
