## MODIFIED Requirements

### Requirement: Plan session events registered in SSE event types
The `plan_round_start`, `plan_session_update`, and `stage_task_update` events SHALL be included in all SSE event type registrations:
- `events.ts` `ALL_EVENT_TYPES` array (backend)
- `agent-events.ts` `AGENT_DETAIL_EVENTS` array (frontend)
- `useSSE.ts` `eventTypes` array (frontend)

#### Scenario: SSE client receives plan round start
- **WHEN** a WebUI SSE client is connected and a plan round starts
- **THEN** the client receives `event: plan_round_start` with the round metadata

#### Scenario: SSE client receives stage_task_update
- **WHEN** a WebUI SSE client is connected and a stage task starts or completes
- **THEN** the client receives `event: stage_task_update` with the task metadata
