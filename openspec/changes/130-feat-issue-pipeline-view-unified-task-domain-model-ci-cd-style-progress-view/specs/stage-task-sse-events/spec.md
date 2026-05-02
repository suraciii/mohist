## ADDED Requirements

### Requirement: Unified stage_task_update SSE event

The system SHALL emit a `stage_task_update` SSE event from all three pipeline stages (Plan, Build, Check). The event payload SHALL conform to:

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

#### Scenario: Plan stage emits stage_task_update when task starts

- **WHEN** the `proposal` task in Plan stage begins execution
- **THEN** a `stage_task_update` event is emitted with `{ stage: 'plan', taskId: 'proposal', taskTitle: 'proposal.md', status: 'started', attempt: 1, artifacts: [] }`

#### Scenario: Plan stage emits stage_task_update when task completes

- **WHEN** the `proposal` task in Plan stage completes successfully
- **THEN** a `stage_task_update` event is emitted with `{ stage: 'plan', taskId: 'proposal', taskTitle: 'proposal.md', status: 'completed', attempt: 1, artifacts: ['proposal.md'] }`

#### Scenario: Build stage emits stage_task_update when task starts

- **WHEN** Build task T-001 begins execution
- **THEN** a `stage_task_update` event is emitted with `{ stage: 'build', taskId: 'T-001', taskTitle: 'Implement auth module', status: 'started', attempt: 1, artifacts: [] }`

#### Scenario: Build stage emits stage_task_update when task fails and retries

- **WHEN** Build task T-002 fails on the first attempt
- **THEN** a `stage_task_update` event is emitted with `{ stage: 'build', taskId: 'T-002', status: 'failed', attempt: 1 }`
- **WHEN** the retry of T-002 begins
- **THEN** a `stage_task_update` event is emitted with `{ stage: 'build', taskId: 'T-002', status: 'retrying', attempt: 2 }`

#### Scenario: Build stage emits stage_task_update when task completes

- **WHEN** Build task T-001 completes
- **THEN** a `stage_task_update` event is emitted with `{ stage: 'build', taskId: 'T-001', status: 'completed', attempt: 1 }`

#### Scenario: Check stage emits stage_task_update for review task

- **WHEN** the `review` task in Check stage begins
- **THEN** a `stage_task_update` event is emitted with `{ stage: 'check', taskId: 'review', taskTitle: 'review', status: 'started', attempt: 1 }`

#### Scenario: Check stage emits stage_task_update when review completes

- **WHEN** the `review` task in Check stage completes
- **THEN** a `stage_task_update` event is emitted with `{ stage: 'check', taskId: 'review', status: 'completed', attempt: 1, artifacts: ['review.md'] }`

### Requirement: stage_task_update registered in SSE event types

The `stage_task_update` event SHALL be included in all SSE event type registrations:
- `events.ts` `ALL_EVENT_TYPES` array (backend)
- `agent-events.ts` `AGENT_DETAIL_EVENTS` array (frontend)
- `useSSE.ts` `eventTypes` array (frontend)

#### Scenario: SSE client receives stage_task_update

- **WHEN** a WebUI SSE client is connected and a Plan task starts
- **THEN** the client receives `event: stage_task_update` with the task metadata

### Requirement: stage_task_update is fire-and-forget

All `stage_task_update` event emissions SHALL be fire-and-forget. Emit failures SHALL NOT affect the pipeline execution flow.

#### Scenario: EventBus emit throws during stage_task_update

- **WHEN** the stage runner emits `stage_task_update` and `eventBus.emit` throws
- **THEN** the error is caught and logged
- **AND** the pipeline continues normally

### Requirement: Legacy SSE events continue emitting

Existing SSE events (`plan_round_start`, `plan_session_update`, `ralph_task_update`, `ralph_loop_progress`, `coder_text_chunk`, `coder_tool_call`) SHALL continue to be emitted unchanged. `stage_task_update` is additive and does not replace legacy events.

#### Scenario: plan_round_start still emitted alongside stage_task_update

- **WHEN** a Plan round starts
- **THEN** both `plan_round_start` and `stage_task_update` (with `status: 'started'`) are emitted

#### Scenario: ralph_task_update still emitted alongside stage_task_update

- **WHEN** a Build task status changes
- **THEN** both `ralph_task_update` and `stage_task_update` are emitted
