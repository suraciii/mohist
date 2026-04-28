## MODIFIED Requirements

### Requirement: Plan/Review stage bridges sessionUpdate via onSessionUpdate

For each sessionUpdate received from the multi-round ACP connection in `runPlanStage()` and `runPipelineReviewStage()`, the `onSessionUpdate` callback SHALL emit a `plan_session_update` event to EventBus with `{ issueId, projectId, roundType, roundIndex, sessionUpdate, data }`. The `data` field SHALL contain the full sessionUpdate payload.

Review stage round types SHALL include: `review` (R0), `review-self-check` (R1), `auto-fix` (R2, R4, ...), `re-verify` (R3, R5, ...).

#### Scenario: Agent message chunk in specs round

- **WHEN** ACP connection reports an `agent_message_chunk` sessionUpdate during the specs round
- **THEN** EventBus emits `plan_session_update` with `roundType: 'specs'`, `sessionUpdate: 'agent_message_chunk'`, and `data` containing the text content

#### Scenario: Tool call completed in design round

- **WHEN** ACP connection reports a `tool_call_update` with `status: 'completed'` during the design round
- **THEN** EventBus emits `plan_session_update` with `roundType: 'design'`, `sessionUpdate: 'tool_call_update'`, and `data` containing rawInput, rawOutput, kind, title

#### Scenario: Review stage uses same mechanism

- **WHEN** `runPipelineReviewStage` receives a sessionUpdate
- **THEN** EventBus emits `plan_session_update` with `roundType` matching the current round (`review`, `review-self-check`, `auto-fix`, or `re-verify`)

#### Scenario: Auto-fix round emits events

- **WHEN** `runPipelineReviewStage` runs an auto-fix round (R2)
- **THEN** EventBus emits `plan_session_update` with `roundType: 'auto-fix'`, `roundIndex: 2`

#### Scenario: Re-verify round emits events

- **WHEN** `runPipelineReviewStage` runs a re-verify round (R3)
- **THEN** EventBus emits `plan_session_update` with `roundType: 're-verify'`, `roundIndex: 3`

### Requirement: Plan stage emits round start events

When `runPlanStage()` begins a new round (proposal / specs / design / tasks / self-review), the system SHALL emit a `plan_round_start` event via the `onSessionUpdate` bridge with `{ issueId, projectId, roundType, roundLabel, roundIndex }`.

Review stage SHALL emit `plan_round_start` for auto-fix and re-verify rounds in addition to existing review and review-self-check rounds.

#### Scenario: Proposal round starts

- **WHEN** `runPlanStage()` begins the first round with `type: 'proposal'`
- **THEN** EventBus emits `plan_round_start` with `roundType: 'proposal'`, `roundLabel: 'proposal.md'`, `roundIndex: 0`

#### Scenario: Self-review round starts

- **WHEN** `runPlanStage()` begins the self-review round
- **THEN** EventBus emits `plan_round_start` with `roundType: 'self-review'`, `roundIndex: 4`

#### Scenario: Auto-fix round starts

- **WHEN** `runPipelineReviewStage()` begins an auto-fix round
- **THEN** EventBus emits `plan_round_start` with `roundType: 'auto-fix'`, `roundLabel: 'auto-fix'`, `roundIndex` matching the round number

#### Scenario: Re-verify round starts

- **WHEN** `runPipelineReviewStage()` begins a re-verify round
- **THEN** EventBus emits `plan_round_start` with `roundType: 're-verify'`, `roundLabel: 're-verify'`, `roundIndex` matching the round number
