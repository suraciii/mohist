## MODIFIED Requirements

### Requirement: Followup message delivery via SignalR

Server SHALL deliver followup messages to the associated runner by pushing a `ReceiveFollowup` SignalR message carrying a session target and the followup `text` to the runner's active connection. The session target SHALL identify the live session in a way that admits both workflow-shaped sessions (identified by a `workflowRunId` + `sessionName` pair) and generic, non-workflow sessions (identified by a project-scoped session identity with no `workflowRunId`). The runner SHALL locate the active ACP session for the target via `AcpSessionManager` and invoke `connection.prompt()` fire-and-forget — the handler SHALL NOT await turn completion and SHALL NOT maintain a runner-side message queue. Existing workflow-session follow-up delivery SHALL continue to work unchanged.

#### Scenario: Server pushes followup to runner

- **WHEN** the followup API accepts a message for an active session with an online runner
- **THEN** the server SHALL send a `ReceiveFollowup` SignalR message to the runner's connectionId
- **AND** the message payload SHALL include a session target that identifies the live session and the followup `text`
- **AND** the payload SHALL be sufficient for the runner to locate the active ACP session regardless of whether the session is workflow-shaped or generic

#### Scenario: Runner fire-and-forgets prompt

- **WHEN** the runner receives `ReceiveFollowup` for a currently active ACP session
- **THEN** the runner SHALL call `session.connection.prompt({ sessionId, prompt: [{ type: "text", text }] })` without awaiting completion
- **AND** the handler SHALL return immediately so the SignalR receiver is not blocked

#### Scenario: Runner drops followup for unknown session

- **WHEN** the runner receives `ReceiveFollowup` and no active ACP session matches the carried session target
- **THEN** the runner SHALL silently drop the message
- **AND** the runner SHALL NOT throw or crash

#### Scenario: Generic session receives a followup

- **WHEN** the followup API accepts a message for an active generic (non-workflow) `AgentSession`
- **AND** the associated runner is connected
- **THEN** the server SHALL push a `ReceiveFollowup` SignalR message carrying a session target that identifies the generic session (no `workflowRunId` required)
- **AND** the runner SHALL locate the active ACP session for that generic target
- **AND** the runner SHALL invoke `connection.prompt()` fire-and-forget the same way it does for a workflow session

#### Scenario: Workflow session follow-up delivery is preserved

- **WHEN** the followup API accepts a message for an active workflow-shaped session
- **THEN** the server SHALL push a `ReceiveFollowup` message identifying the session by its `workflowRunId` + `sessionName` pair exactly as before this change
- **AND** the runner SHALL deliver the followup to the workflow session with no behavioral change
