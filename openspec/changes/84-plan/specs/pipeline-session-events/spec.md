## MODIFIED Requirements

### Requirement: Plan session events registered in SSE event types
The `plan_round_start`, `plan_session_update`, and `plan_round_complete` events SHALL be included in all SSE event type registrations:
- `event-bus.ts` `EventMap` type (backend)
- `api/events.ts` `ALL_EVENT_TYPES` array (backend)
- `types.ts` `AgentDetailEventMap` type (frontend)
- `agent-events.ts` `AGENT_DETAIL_EVENTS` array (frontend)
- `useSSE.tsx` `eventTypes` array (frontend)

#### Scenario: SSE client receives plan round start
- **WHEN** a WebUI SSE client is connected and a plan round starts
- **THEN** the client receives `event: plan_round_start` with the round metadata

#### Scenario: SSE client receives plan round complete
- **WHEN** a WebUI SSE client is connected and a plan round completes
- **THEN** the client receives `event: plan_round_complete` with `{ issueId, projectId, roundType, roundLabel, roundIndex, duration, verdict? }`

### Requirement: Plan stage emits round start events
When `runPlanStage()` begins a new round (proposal / specs / design / tasks / self-review), the system SHALL emit a `plan_round_start` event via the `onSessionUpdate` bridge with `{ issueId, projectId, roundType, roundLabel, roundIndex }`.

#### Scenario: Proposal round starts
- **WHEN** `runPlanStage()` begins the first round with `type: 'proposal'`
- **THEN** EventBus emits `plan_round_start` with `roundType: 'proposal'`, `roundLabel: 'proposal.md'`, `roundIndex: 0`

#### Scenario: Self-review round starts
- **WHEN** `runPlanStage()` begins the self-review round
- **THEN** EventBus emits `plan_round_start` with `roundType: 'self-review'`, `roundIndex: 4`

#### Scenario: Auto-fix round starts
- **WHEN** self-review FAIL triggers the auto-fix round
- **THEN** EventBus emits `plan_round_start` with `roundType: 'auto-fix'`, `roundIndex: 5`

#### Scenario: Re-self-review round starts
- **WHEN** auto-fix completes and re-self-review begins
- **THEN** EventBus emits `plan_round_start` with `roundType: 're-self-review'`, `roundIndex: 6`
