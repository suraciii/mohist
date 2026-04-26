## MODIFIED Requirements

### Requirement: Plan stage emits round start events
When `runPlanStage()` begins a new round (proposal / specs / design / tasks / self-review), the system SHALL emit a `plan_round_start` event via the `onSessionUpdate` bridge with `{ issueId, projectId, roundType, roundLabel, roundIndex }`.

When `runPipelineReviewStage()` begins a round, the system SHALL emit a `plan_round_start` event with the same structure. Review stage has two rounds: `roundType: 'review'` (roundIndex 0) and `roundType: 'review-self-check'` (roundIndex 1).

#### Scenario: Proposal round starts
- **WHEN** `runPlanStage()` begins the first round with `type: 'proposal'`
- **THEN** EventBus emits `plan_round_start` with `roundType: 'proposal'`, `roundLabel: 'proposal.md'`, `roundIndex: 0`

#### Scenario: Self-review round starts
- **WHEN** `runPlanStage()` begins the self-review round
- **THEN** EventBus emits `plan_round_start` with `roundType: 'self-review'`, `roundIndex: 4`

#### Scenario: Review stage round 0 starts
- **WHEN** `runPipelineReviewStage()` begins the first round
- **THEN** EventBus emits `plan_round_start` with `roundType: 'review'`, `roundLabel: 'review'`, `roundIndex: 0`

#### Scenario: Review stage self-check round starts
- **WHEN** `runPipelineReviewStage()` begins the self-check round
- **THEN** EventBus emits `plan_round_start` with `roundType: 'review-self-check'`, `roundLabel: 'review-self-check'`, `roundIndex: 1`

### Requirement: Plan/Review stage bridges sessionUpdate via onSessionUpdate
For each sessionUpdate received from the multi-round ACP connection in `runPlanStage()` and `runPipelineReviewStage()`, the `onSessionUpdate` callback SHALL emit a `plan_session_update` event to EventBus with `{ issueId, projectId, roundType, roundIndex, sessionUpdate, data }`. The `data` field SHALL contain the full sessionUpdate payload.

During the Review stage self-check round (round 1), `roundType` SHALL be `'review-self-check'` and `roundIndex` SHALL be `1`.

#### Scenario: Agent message chunk in specs round
- **WHEN** ACP connection reports an `agent_message_chunk` sessionUpdate during the specs round
- **THEN** EventBus emits `plan_session_update` with `roundType: 'specs'`, `sessionUpdate: 'agent_message_chunk'`, and `data` containing the text content

#### Scenario: Tool call completed in design round
- **WHEN** ACP connection reports a `tool_call_update` with `status: 'completed'` during the design round
- **THEN** EventBus emits `plan_session_update` with `roundType: 'design'`, `sessionUpdate: 'tool_call_update'`, and `data` containing rawInput, rawOutput, kind, title

#### Scenario: Review stage round 0 session update
- **WHEN** `runPipelineReviewStage` receives a sessionUpdate during the review round (round 0)
- **THEN** EventBus emits `plan_session_update` with `roundType: 'review'`, `roundIndex: 0`

#### Scenario: Review stage self-check round session update
- **WHEN** `runPipelineReviewStage` receives a sessionUpdate during the self-check round (round 1)
- **THEN** EventBus emits `plan_session_update` with `roundType: 'review-self-check'`, `roundIndex: 1`
