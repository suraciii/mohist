## MODIFIED Requirements

### Requirement: AcpConnectionOptions extended with issueNumber and onSessionUpdate
`AcpConnectionOptions` and `AcpSessionOptions` SHALL include two new optional fields:
- `issueNumber?: number` — used for SSE event `issueId` (frontend matches by issue number, not UUID)
- `onSessionUpdate?: (notification: SessionNotification) => void` — callback for external event processing (used by Plan/Review stage bridge)

When `onSessionUpdate` is provided, `createAcpConnection` SHALL call it for every sessionUpdate notification (including `agent_thought_chunk`, `tool_call`, `tool_call_update`) and SHALL NOT emit `coder_text_chunk` or `coder_tool_call` events internally. When not provided, behavior is unchanged.

#### Scenario: Plan stage uses onSessionUpdate
- **WHEN** `createAcpConnection` is called with `onSessionUpdate` set
- **THEN** for each ACP sessionUpdate (including `agent_thought_chunk`): agentText accumulates normally, `workflowLogRepo.insert()` executes, `onSessionUpdate(notification)` is called
- **AND** `coder_text_chunk` and `coder_tool_call` are NOT emitted

#### Scenario: Build stage uses default behavior
- **WHEN** `runAcpSession` is called without `onSessionUpdate`
- **THEN** behavior is unchanged: `coder_text_chunk` and `coder_tool_call` are emitted as before

### Requirement: Plan/Review stage bridges sessionUpdate via onSessionUpdate
For each sessionUpdate received from the multi-round ACP connection in `runPlanStage()` and `runPipelineReviewStage()`, the `onSessionUpdate` callback SHALL emit a `plan_session_update` event to EventBus with `{ issueId, projectId, roundType, roundIndex, sessionUpdate, data }`. The `data` field SHALL contain the full sessionUpdate payload. This includes `tool_call`, `tool_call_update`, `agent_message_chunk`, and `agent_thought_chunk` sessionUpdate types.

#### Scenario: Agent message chunk in specs round
- **WHEN** ACP connection reports an `agent_message_chunk` sessionUpdate during the specs round
- **THEN** EventBus emits `plan_session_update` with `roundType: 'specs'`, `sessionUpdate: 'agent_message_chunk'`, and `data` containing the text content

#### Scenario: Tool call in design round
- **WHEN** ACP connection reports a `tool_call` sessionUpdate during the design round
- **THEN** EventBus emits `plan_session_update` with `roundType: 'design'`, `sessionUpdate: 'tool_call'`, and `data` containing kind, title, rawInput, status

#### Scenario: Tool call completed in design round
- **WHEN** ACP connection reports a `tool_call_update` with `status: 'completed'` during the design round
- **THEN** EventBus emits `plan_session_update` with `roundType: 'design'`, `sessionUpdate: 'tool_call_update'`, and `data` containing rawInput, rawOutput, kind, title

#### Scenario: Agent thought chunk in proposal round
- **WHEN** ACP connection reports an `agent_thought_chunk` sessionUpdate during the proposal round
- **THEN** EventBus emits `plan_session_update` with `roundType: 'proposal'`, `sessionUpdate: 'agent_thought_chunk'`, and `data` containing the thought text

#### Scenario: Review stage uses same mechanism
- **WHEN** `runPipelineReviewStage` receives a sessionUpdate
- **THEN** EventBus emits `plan_session_update` with `roundType: 'review'`


