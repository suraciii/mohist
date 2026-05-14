## MODIFIED Requirements

### Requirement: REQ-PSE-214 Pipeline session tool updates expose normalized identity

Pipeline session event bridges SHALL receive normalized ACP tool update payloads so live plan/check session surfaces converge with persisted replay for tool name and lifecycle identity.

#### Scenario: Raw notification bridge receives normalized tool update
- **WHEN** Plan or Check stage receives a `tool_call` or `tool_call_update` notification
- **THEN** the emitted `plan_session_update` data SHALL include normalized `toolCall.toolName` and `toolCall.toolCallId` when they can be recovered or synthesized

#### Scenario: Live and persisted tool identity agree
- **WHEN** a live plan/check session later reloads from persisted session data
- **THEN** tool updates SHALL preserve equivalent tool name and `toolCallId` identity across live SSE data and replayed logs
