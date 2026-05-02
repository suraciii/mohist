## MODIFIED Requirements

### Requirement: AcpConnectionOptions extended with issueNumber and onSessionUpdate
`AcpConnectionOptions` and `AcpSessionOptions` SHALL include three optional fields:
- `issueNumber?: number` — used for SSE event `issueId` (frontend matches by issue number, not UUID)
- `onSessionUpdate?: (notification: SessionNotification) => void` — callback for external event processing (used by Plan/Review stage bridge)
- `title?: string` — human-readable session title persisted to `coder_session` and emitted via SSE

When `onSessionUpdate` is provided, `createAcpConnection` SHALL call it for every sessionUpdate notification and SHALL NOT emit `coder_text_chunk` or `coder_tool_call` events internally. When not provided, behavior is unchanged.

#### Scenario: Plan stage uses onSessionUpdate
- **WHEN** `createAcpConnection` is called with `onSessionUpdate` set
- **THEN** for each ACP sessionUpdate: agentText accumulates normally, `workflowLogRepo.insert()` executes, `onSessionUpdate(notification)` is called
- **AND** `coder_text_chunk` and `coder_tool_call` are NOT emitted

#### Scenario: Build stage uses default behavior
- **WHEN** `runAcpSession` is called without `onSessionUpdate`
- **THEN** behavior is unchanged: `coder_text_chunk` and `coder_tool_call` are emitted as before

#### Scenario: runAcpSession passes title to coder_session
- **WHEN** `runAcpSession` is called with `title: "T-004: Create Plan"`
- **THEN** the `coderSessionRepo.insert()` call includes `title: "T-004: Create Plan"`

#### Scenario: createAcpConnection passes title to coder_session
- **WHEN** `createAcpConnection` is called with `title: "Plan stage"`
- **THEN** the `coderSessionRepo.insert()` call includes `title: "Plan stage"`

### Requirement: SSE event issueId uses issue number via dual-track
In `acp-session.ts`, SSE event emission SHALL use `String(options.issueNumber ?? options.issueId)` as the `issueId` field. The `coder_session_started` SSE event SHALL additionally carry a `title` field from `options.title`. DB operations (`workflowLogRepo.insert`, `coderSessionRepo.insert`) SHALL continue using `options.issueId` (UUID) unchanged.

#### Scenario: coder_text_chunk with issueNumber
- **WHEN** `issueNumber: 5` is passed in options
- **THEN** `coder_text_chunk` event has `issueId: "5"`

#### Scenario: Fallback when issueNumber not provided
- **WHEN** `issueNumber` is undefined (e.g., Explore sessions)
- **THEN** SSE event `issueId` falls back to `issueId` (UUID)

#### Scenario: coder_session_started includes title
- **WHEN** `runAcpSession` is called with `title: "T-004: Create Plan"` and `coderSessionRepo` is available
- **THEN** the `coder_session_started` event payload includes `title: "T-004: Create Plan"`

#### Scenario: coder_session_started with no title
- **WHEN** `runAcpSession` is called without a `title` option
- **THEN** the `coder_session_started` event payload includes `title: undefined` (omitted or null)
