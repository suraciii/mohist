# OpenSpec Capability: pipeline-session-events (delta)

## MODIFIED Requirements

### Requirement: agent_usage_update session event carries token, cost, and context window deltas

The pipeline SHALL define a new `agent_usage_update` session event that surfaces per-turn ACP usage and cost data on the live event stream.

#### Scenario: New event type payload
- **WHEN** the runner forwards a usage event from `PromptResponse.usage` or a `usage_update` session update notification
- **THEN** the emitted `agent_usage_update` event SHALL include the per-turn token deltas (`inputTokens`, `outputTokens`, `totalTokens`, `cachedReadTokens`, `thoughtTokens` — all optional numbers), the per-turn cost (`costAmount` and `costCurrency` — both optional), and the latest context window snapshot (`contextWindowSize` and `contextWindowUsed` — both optional numbers)
- **AND** SHALL include the session routing fields (`issueId`, `projectId`, `executionId`, `acpSessionId`, `coderSessionId`) so live clients can match the event to a session

#### Scenario: agent_usage_update is registered in all SSE event type arrays
- **WHEN** the new event is added
- **THEN** `agent_usage_update` SHALL be present in:
  - `packages/server/.../Infrastructure/Events/EventBusEventTypes.cs` `All` array
  - `packages/web/.../entities/agent/model/events.ts` `AGENT_DETAIL_EVENTS` array
  - `packages/web/.../entities/agent/model/types.ts` `AgentDetailEventMap`
  - `packages/web/.../app/providers/LiveTaskProvider.tsx` agent-activity invalidation list
- **AND** the same type SHALL also be registered in the `agent_usage_update` invalidation list so `agent-activity` queries are refreshed when usage updates arrive

#### Scenario: Server-side grain handles agent_usage_update
- **WHEN** `WorkflowAgentSessionGrain.AppendEventsAsync` receives an `agent_usage_update` event
- **THEN** the grain SHALL apply the per-turn usage to the session's accumulated token counters, update `CostAmount` / `CostCurrency`, and store `ContextWindowUsed`
- **AND** the event row SHALL be persisted to `WorkflowAgentSessionEvents` for audit

#### Scenario: Live clients refresh session queries on usage updates
- **WHEN** an `agent_usage_update` SSE event arrives at the Web UI
- **THEN** the LiveTaskProvider SHALL invalidate the `agent-activity` query key for the affected issue
- **AND** UI components that read session summaries (lists, cards) SHALL refetch and show the updated usage fields

### Requirement: agent_session_terminal carries a structured failureCategory

The `agent_session_terminal` event SHALL include a structured `failureCategory` field alongside the existing free-text `failureReason` so live clients and analytics can group failures by cause.

#### Scenario: Terminal event payload with failureCategory
- **WHEN** the runner emits an `agent_session_terminal` event with `status = "failed"` and a known `LivenessFailureReason` value
- **THEN** the event payload SHALL include `failureCategory` set to that value (`probe_timeout`, `probe_send_failed`, `protocol_disconnect`, or `process_exit`)
- **AND** the existing `failureReason` field SHALL remain the underlying diagnostic text

#### Scenario: Terminal event without failureCategory
- **WHEN** the runner emits an `agent_session_terminal` event with `status = "completed"`
- **THEN** the event payload SHALL NOT include a `failureCategory` (or SHALL set it to `null`)
- **AND** `failureReason` SHALL also be `null`

#### Scenario: Server-side grain persists failureCategory
- **WHEN** the server's `WorkflowAgentSessionGrain.AppendEventsAsync` processes a terminal event with `failureCategory`
- **THEN** it SHALL set the session row's `FailureCategory` field to the provided value
- **AND** the value SHALL be returned to clients through `WorkflowAgentSessionDto`, `WorkflowAgentSessionSummaryDto`, and the agent activity `AgentActivitySession` payload

### Requirement: SSE event type registries include agent_usage_update

The pipeline session event registries SHALL list the new `agent_usage_update` event so live SSE clients can subscribe to it.

#### Scenario: Backend registry includes agent_usage_update
- **WHEN** the backend publishes any usage event
- **THEN** the `EventBusEventTypes.All` array SHALL include the literal string `"agent_usage_update"`
- **AND** the SSE bridge SHALL forward events of that type to subscribed clients without filtering

#### Scenario: Frontend registry includes agent_usage_update
- **WHEN** the Web UI initializes the agent detail event surface
- **THEN** `AGENT_DETAIL_EVENTS` SHALL include `"agent_usage_update"` so `dispatchAgentEvent` and the live SSE bridge can route the event
- **AND** the corresponding entry SHALL be added to `AgentDetailEventMap` with a typed payload so event handlers are type-safe
