## MODIFIED Requirements

### Requirement: AcpConnectionOptions extended with issueNumber and onSessionUpdate
`AcpConnectionOptions` and `AcpSessionOptions` SHALL include the following optional fields:
- `issueNumber?: number` — used for SSE event `issueId` (frontend matches by issue number, not UUID)
- `onSessionUpdate?: (notification: SessionNotification) => void` — callback for external event processing (used by Plan/Review stage bridge)
- `title?: string` — human-readable label for the coder session, stored in the `coder_session` table and emitted via SSE

When `onSessionUpdate` is provided, `createAcpConnection` SHALL call it for every sessionUpdate notification and SHALL NOT emit `coder_text_chunk` or `coder_tool_call` events internally. When not provided, behavior is unchanged.

#### Scenario: Plan stage uses onSessionUpdate
- **WHEN** `createAcpConnection` is called with `onSessionUpdate` set
- **THEN** for each ACP sessionUpdate: agentText accumulates normally, `workflowLogRepo.insert()` executes, `onSessionUpdate(notification)` is called
- **AND** `coder_text_chunk` and `coder_tool_call` are NOT emitted

#### Scenario: Build stage uses default behavior
- **WHEN** `runAcpSession` is called without `onSessionUpdate`
- **THEN** behavior is unchanged: `coder_text_chunk` and `coder_tool_call` are emitted as before

#### Scenario: Session created with title
- **WHEN** `createAcpConnection` is called with `title: "Plan stage"`
- **THEN** the coder_session row has `title = "Plan stage"`

#### Scenario: Session created without title
- **WHEN** `runAcpSession` is called without `title`
- **THEN** the coder_session row has `title = NULL`
