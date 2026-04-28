## MODIFIED Requirements

### Requirement: Plan/Review stage bridges sessionUpdate via onSessionUpdate
For each sessionUpdate received from the multi-round ACP connection in `runPlanStage()` and `runPipelineReviewStage()`, the `onSessionUpdate` callback SHALL emit a `plan_session_update` event to EventBus with `{ issueId, projectId, roundType, roundIndex, sessionUpdate, data }`. The `data` field SHALL contain the full sessionUpdate payload. Review stage auto-fix and re-verify rounds SHALL emit with their respective roundType values.

#### Scenario: Agent message chunk in specs round
- **WHEN** ACP connection reports an `agent_message_chunk` sessionUpdate during the specs round
- **THEN** EventBus emits `plan_session_update` with `roundType: 'specs'`, `sessionUpdate: 'agent_message_chunk'`, and `data` containing the text content

#### Scenario: Tool call completed in design round
- **WHEN** ACP connection reports a `tool_call_update` with `status: 'completed'` during the design round
- **THEN** EventBus emits `plan_session_update` with `roundType: 'design'`, `sessionUpdate: 'tool_call_update'`, and `data` containing rawInput, rawOutput, kind, title

#### Scenario: Review stage uses same mechanism
- **WHEN** `runPipelineReviewStage` receives a sessionUpdate
- **THEN** EventBus emits `plan_session_update` with `roundType: 'review'`

#### Scenario: Auto-fix round emits events
- **WHEN** `runPipelineReviewStage` runs an auto-fix agent round
- **THEN** EventBus emits `plan_session_update` events with `roundType: 'auto-fix'`
- **AND** `plan_round_start` is emitted with `roundType: 'auto-fix'`, `roundLabel: 'auto-fix'`, `roundIndex: 2` (first attempt) or `roundIndex: 4` (second attempt)

#### Scenario: Re-verify round emits events
- **WHEN** `runPipelineReviewStage` runs a re-verify agent round
- **THEN** EventBus emits `plan_session_update` events with `roundType: 're-verify'`
- **AND** `plan_round_start` is emitted with `roundType: 're-verify'`, `roundLabel: 're-verify'`, `roundIndex: 3` (first attempt) or `roundIndex: 5` (second attempt)
