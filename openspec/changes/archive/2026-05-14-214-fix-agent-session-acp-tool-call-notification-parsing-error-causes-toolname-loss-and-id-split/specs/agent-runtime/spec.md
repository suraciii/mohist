## MODIFIED Requirements

### Requirement: REQ-AR-214 ACP tool notifications are normalized before observer dispatch

Agent runtime SHALL normalize `tool_call` and `tool_call_update` ACP session updates before session observers receive `onSessionEvent`, `onRawNotification`, or `onToolCall` callbacks.

#### Scenario: Top-level tool identity is preserved
- **WHEN** an ACP tool notification carries `toolName`, `name`, `toolCallId`, `id`, or `callId` at the top level instead of inside `toolCall`
- **THEN** the normalized update SHALL expose the best available `toolCall.toolName`
- **AND** SHALL expose a canonical `toolCall.toolCallId`

#### Scenario: Nested and provider ids are preferred
- **WHEN** an ACP tool notification carries a provider id in nested or top-level `toolCallId`, `id`, or `callId`
- **THEN** Agent runtime SHALL reuse that id as the canonical `toolCall.toolCallId`
- **AND** SHALL NOT replace it with a synthetic id

#### Scenario: Missing id is synthesized once
- **WHEN** an ACP tool notification has no provider id
- **THEN** Agent runtime SHALL synthesize one stable `toolCallId` for the notification lifecycle
- **AND** SHALL use that same id for persisted updates and emitted tool-call observer events

#### Scenario: Tool call updates are normalized
- **WHEN** a `tool_call_update` notification is received
- **THEN** it SHALL go through the same identity normalization as `tool_call`
- **AND** completed output and metadata SHALL remain available to observers and logs
