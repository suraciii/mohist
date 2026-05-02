## ADDED Requirements

### Requirement: stage_task_update unified SSE event

All stage runners (Plan, Build, Check) SHALL emit a `stage_task_update` SSE event whenever a task's status changes. The event SHALL have a unified payload structure:

```typescript
stage_task_update: {
  issueId: string
  projectId: string
  stage: Stage
  taskId: string
  taskTitle: string
  status: 'started' | 'completed' | 'failed' | 'retrying'
  attempt: number
  artifacts: string[]
}
```

This event replaces the need for three separate event schemas (`plan_round_start`, `ralph_task_update`, `plan_round_complete`). The old events SHALL continue to be emitted for backward compatibility.

#### Scenario: Plan task starts

- **WHEN** PlanStageRunner begins the `proposal` task
- **THEN** EventBus emits `stage_task_update` with `{ stage: 'plan', taskId: 'proposal', taskTitle: 'proposal.md', status: 'started', attempt: 1, artifacts: [] }`

#### Scenario: Plan task completes

- **WHEN** PlanStageRunner completes the `design` task and verifies `design.md` exists
- **THEN** EventBus emits `stage_task_update` with `{ stage: 'plan', taskId: 'design', status: 'completed', artifacts: ['<changeDir>/design.md'] }`

#### Scenario: Build task starts

- **WHEN** RalphExecutor begins processing task T-001
- **THEN** EventBus emits `stage_task_update` with `{ stage: 'build', taskId: 'T-001', taskTitle: '<task title>', status: 'started', attempt: 1 }`

#### Scenario: Build task retries

- **WHEN** RalphExecutor retries task T-002 after a failure
- **THEN** EventBus emits `stage_task_update` with `{ stage: 'build', taskId: 'T-002', status: 'retrying', attempt: 2 }`

#### Scenario: Check task starts

- **WHEN** CheckStageRunner begins the `review` task
- **THEN** EventBus emits `stage_task_update` with `{ stage: 'check', taskId: 'review', taskTitle: 'review', status: 'started', attempt: 1 }`

#### Scenario: Check task completes

- **WHEN** CheckStageRunner completes the `review-self-check` task
- **THEN** EventBus emits `stage_task_update` with `{ stage: 'check', taskId: 'review-self-check', status: 'completed', artifacts: ['<changeDir>/review-self-check.md'] }`

### Requirement: stage_task_update registered in SSE event types

The `stage_task_update` event SHALL be included in all SSE event type registrations:
- `events.ts` `ALL_EVENT_TYPES` array (backend)
- `agent-events.ts` `AGENT_DETAIL_EVENTS` array (frontend)
- `useSSE.ts` `eventTypes` array (frontend)
- `event-bus.ts` `EventMap` type definition

#### Scenario: SSE client receives stage_task_update

- **WHEN** a WebUI SSE client is connected and a Plan task starts
- **THEN** the client receives `event: stage_task_update` with the unified payload

### Requirement: stage_task_update is fire-and-forget

All EventBus emit calls for `stage_task_update` SHALL be fire-and-forget. Emit failures SHALL NOT affect the pipeline execution flow. Errors SHALL be caught and logged.

#### Scenario: EventBus emit throws during stage_task_update

- **WHEN** `stage_task_update` emit encounters an error
- **THEN** the error is caught and logged
- **AND** the pipeline continues normally

### Requirement: Old SSE events continue to emit

The existing SSE events (`plan_round_start`, `plan_session_update`, `plan_round_complete`, `ralph_task_update`, `ralph_loop_progress`) SHALL continue to be emitted unchanged. The new `stage_task_update` event is additive — emitted alongside the old events.

#### Scenario: Plan stage emits both old and new events

- **WHEN** PlanStageRunner begins the `proposal` task
- **THEN** EventBus emits both `plan_round_start` (with existing payload) and `stage_task_update` (with unified payload)
- **AND** both events reach SSE clients

#### Scenario: Build stage emits both old and new events

- **WHEN** RalphExecutor starts task T-001
- **THEN** EventBus emits both `ralph_task_update` (with existing payload) and `stage_task_update` (with unified payload)

## MODIFIED Requirements

### Requirement: Plan stage emits round start events

When `runPlanStage()` begins a new task (proposal / specs / design / tasks / self-review), the system SHALL emit both a `plan_round_start` event (for backward compatibility) AND a `stage_task_update` event via the EventBus. The `stage_task_update` event SHALL include `{ stage: 'plan', taskId, taskTitle, status: 'started', attempt, artifacts }`.

#### Scenario: Proposal task starts

- **WHEN** `runPlanStage()` begins the first task with `type: 'proposal'`
- **THEN** EventBus emits `plan_round_start` with `roundType: 'proposal'`, `roundLabel: 'proposal.md'`, `roundIndex: 0` (unchanged)
- **AND** EventBus emits `stage_task_update` with `{ stage: 'plan', taskId: 'proposal', taskTitle: 'proposal.md', status: 'started', attempt: 1, artifacts: [] }`

#### Scenario: Self-review task starts

- **WHEN** `runPlanStage()` begins the self-review task
- **THEN** EventBus emits `plan_round_start` with `roundType: 'self-review'`, `roundIndex: 4` (unchanged)
- **AND** EventBus emits `stage_task_update` with `{ stage: 'plan', taskId: 'self-review', status: 'started', attempt: 1 }`

### Requirement: Plan session events registered in SSE event types

The `plan_round_start`, `plan_session_update`, and `stage_task_update` events SHALL be included in all SSE event type registrations:
- `events.ts` `ALL_EVENT_TYPES` array (backend)
- `agent-events.ts` `AGENT_DETAIL_EVENTS` array (frontend)
- `useSSE.ts` `eventTypes` array (frontend)

#### Scenario: SSE client receives plan round start

- **WHEN** a WebUI SSE client is connected and a plan task starts
- **THEN** the client receives `event: plan_round_start` with the round metadata
- **AND** the client receives `event: stage_task_update` with the unified task metadata

### Requirement: Build stage passes eventBus to RalphExecutor

`runPipelineBuildStage` SHALL pass `this.eventBus` to `RalphExecutor` via its context. `RalphExecutorContext` SHALL be extended with `workflowLogRepo`, `coderSessionRepo`, and `issueNumber` fields. These SHALL be forwarded to `_acpSessionRunner` (runAcpSession) calls. RalphExecutor SHALL also emit `stage_task_update` alongside existing `ralph_task_update`.

#### Scenario: Build stage emits both ralph_task_update and stage_task_update

- **WHEN** `runPipelineBuildStage` is called and eventBus is available
- **THEN** `ralph_task_update`, `stage_task_update`, and `ralph_loop_progress` SSE events are all emitted during Build

#### Scenario: Build stage coder sessions get eventBus

- **WHEN** RalphExecutor calls `_acpSessionRunner` for a task
- **THEN** the runner receives `eventBus`, `workflowLogRepo`, `coderSessionRepo`, and `issueNumber` from context
