# OpenSpec Capability: signalr-realtime-push

## ADDED Requirements

### Requirement: Web subscribes via SetSubscriptionsAsync on connect and reconnect

The Web UI's `useEventsConnection` hook SHALL call `connection.invoke('SetSubscriptionsAsync', [...])` with the union of all event types the Web consumes (the `EventName` union from `entities/issue/@x/events.ts` plus `AGENT_DETAIL_EVENTS` from `entities/agent/model/events.ts`) after the SignalR `start()` promise resolves, and SHALL re-invoke the same call from the SignalR `onreconnected` callback. The hook SHALL resolve the canonical event-type list from a single helper so `LiveTaskProvider` and the subscription call share one source of truth. The set MUST include every event the Web's downstream `LiveTaskProvider` switch can route, including both the legacy snake_case names and the new `com.mohist.*` reverse-DNS names.

#### Scenario: Fresh connection subscribes on first start
- **WHEN** the Web opens the SignalR connection for a project
- **AND** the SignalR `start()` promise resolves successfully
- **THEN** the Web invokes `SetSubscriptionsAsync` with the full event-type list
- **AND** the server's `ConnectionSubscriptionRegistry` for that connection contains that list

#### Scenario: Reconnect re-subscribes
- **WHEN** the SignalR `onreconnected` callback fires after a transport loss
- **THEN** the Web invokes `SetSubscriptionsAsync` with the full event-type list again
- **AND** the connection's subscription set is restored before any subsequent emit

#### Scenario: SetSubscriptions is idempotent
- **WHEN** the Web invokes `SetSubscriptionsAsync` twice with the same list
- **THEN** the server's per-connection subscription set reflects the list exactly once
- **AND** re-invoking on reconnect SHALL NOT duplicate or shift the subscription entries

#### Scenario: No emit reaches a connection that has not subscribed
- **WHEN** a connection has opened but has not yet called `SetSubscriptionsAsync`
- **AND** the bus emits an event
- **THEN** the dispatcher SHALL NOT push that event to that connection
- **AND** once the client calls `SetSubscriptionsAsync`, the next bus emit SHALL reach the connection

### Requirement: Reverse-DNS and legacy event names both reach the Web

The Web's `LiveTaskProvider` switch SHALL handle both the legacy snake_case event names (for any unmigrated producer) and the new `com.mohist.*` reverse-DNS event names. The `EventMap` and `AgentDetailEventMap` types SHALL include the reverse-DNS names so the TypeScript switch exhausts the new arms. New switch arms for reverse-DNS names SHALL map to the same cache invalidation and toast logic that the legacy arms already use.

#### Scenario: Stage start reaches the Web under both names
- **WHEN** a `com.mohist.workflow.stage.started` event arrives at a subscribed connection
- **THEN** `LiveTaskProvider` invalidates the same `issues` query keys as the legacy `stage_changed` arm
- **AND** the behaviour is observably equivalent to receiving `stage_changed`

#### Scenario: Stage completion and failure
- **WHEN** `com.mohist.workflow.stage.completed` or `com.mohist.workflow.stage.failed` arrives
- **THEN** the Web invalidates the same query keys as the legacy arms and surfaces the same toasts

#### Scenario: Approval requested and resolved
- **WHEN** `com.mohist.workflow.stage.approval-requested` or `com.mohist.workflow.stage.approval-resolved` arrives
- **THEN** the Web invalidates `issues` and `agent-activity` and surfaces the same approval toasts

#### Scenario: Workflow run lifecycle
- **WHEN** any of `com.mohist.workflow.run.*` arrives (started, resumed, paused, stopped, completed, failed, retrying, rerunning)
- **THEN** the Web invalidates the same query keys the legacy `agent_started` / `agent_completed` / `agent_paused` / `agent_error` arms invalidate
- **AND** the same toast logic for pause/error applies

#### Scenario: Issue lifecycle
- **WHEN** any `com.mohist.issue.*` event arrives (created, closed, archived, unarchived, reopened, work-started, work-completed, labels-changed, priority-changed, prerequisite-added, prerequisite-removed)
- **THEN** the Web invalidates the same query keys the legacy arms would invalidate
- **AND** the issue detail query for the affected issue number is refreshed

#### Scenario: Agent session lifecycle
- **WHEN** any of `com.mohist.agent-session.started` / `completed` / `failed` / `cancelled` / `status-changed` arrives
- **THEN** the Web invalidates `agent-status`, `agent-activity`, and `issues`
- **AND** for `failed` / `cancelled` the same toast pattern as the legacy arms applies

### Requirement: Connection lifecycle tolerates an empty initial subscription set

The SignalR hub `MohistHub.OnConnectedAsync` SHALL NOT treat "the connection's subscription set is empty" as an error or a permanent state. The first `SetSubscriptionsAsync` call from the client SHALL become the source of truth and SHALL populate the `ConnectionSubscriptionRegistry` and the durable `IConnectionSubscriptionGrain` together. Replay-on-reconnect SHALL continue to use the durable grain's stored set when present.

#### Scenario: First SetSubscriptions after OnConnected populates registry
- **WHEN** a connection opens (no stored subscription set in the grain)
- **AND** the client calls `SetSubscriptionsAsync` with a list
- **THEN** the registry and the grain both contain that list
- **AND** the next bus emit is delivered to that connection

#### Scenario: Reconnect replays stored set
- **WHEN** a connection reconnects and the grain has a stored set
- **THEN** the hub reapplies that set to the registry in `OnConnectedAsync`
- **AND** the client re-invokes `SetSubscriptionsAsync` from `onreconnected` to keep both sides in sync

#### Scenario: Empty default is the correct initial state
- **WHEN** a connection has opened but not yet called `SetSubscriptionsAsync`
- **THEN** the connection's subscription set in the registry is empty
- **AND** the dispatcher filters out every emit for that connection
- **AND** this is the expected default for a freshly opened tab

### Requirement: Domain lifecycle events publish through CloudEventBus

Domain events that change an agent session's lifecycle state — `AgentSessionStarted`, `AgentSessionActivated`, `AgentSessionCompleted`, `AgentSessionFailed`, `AgentSessionCancelled`, and `AgentSessionStatusChanged` — SHALL each have a `BusType` mapping in `AgentSessionEventSerializer` that resolves to the corresponding `EventCatalog.ReverseDns.AgentSession*` constant. `AgentSessionGrain.AppendRuntimeEventsAsync` SHALL publish the corresponding domain event through `IEventPublisher` exactly once when the session transitions to a new lifecycle state, after the row is persisted, deduped against any already-emitted domain event for the same transition.

#### Scenario: Session start publishes AgentSessionStarted
- **WHEN** `AppendRuntimeEventsAsync` receives its first event row for a new session
- **THEN** the grain publishes `com.mohist.agent-session.started` via `IEventPublisher`
- **AND** the event is fanned out through `EventBridge` to subscribed SignalR connections

#### Scenario: Session completion publishes AgentSessionCompleted
- **WHEN** an `agent_session_terminal` event with `status: "completed"` is appended
- **THEN** the grain publishes `com.mohist.agent-session.completed` exactly once for that transition

#### Scenario: Session failure and cancellation
- **WHEN** an `agent_session_terminal` event with `status: "failed"` or `"cancelled"` is appended
- **THEN** the grain publishes `com.mohist.agent-session.failed` or `com.mohist.agent-session.cancelled` exactly once

#### Scenario: Liveness status changes publish AgentSessionStatusChanged
- **WHEN** an `agent_liveness_status` event transitions the session to a new status
- **THEN** the grain publishes `com.mohist.agent-session.status-changed` for that transition
- **AND** the same status SHALL NOT be published twice for the same already-emitted state

### Requirement: Transcript events flow through a dedicated non-domain channel

A new `ITranscriptEventPublisher` interface SHALL expose a single `PublishAsync(TranscriptEnvelope)` method. The in-process implementation SHALL fan out to subscribed SignalR connections via a new `OnTranscriptEvent` method on `IEventsClient`, reusing the existing `ConnectionSubscriptionRegistry` for filtering. `AgentSessionGrain.AppendRuntimeEventsAsync` SHALL publish each appended `coder_text_chunk` / `coder_thought_chunk` / `coder_tool_call` / `ralph_task_update` / `ralph_loop_progress` / `agent_liveness_status` / `agent_usage_update` / `agent_session_model_resolved` row through `ITranscriptEventPublisher` in addition to persisting the row. Transcript events SHALL NOT be published through `IEventPublisher` / `EventBridge`; the `ITranscriptEventPublisher` is the only realtime path for transcript observation data.

#### Scenario: Transcript event reaches a subscribed connection
- **WHEN** `AppendRuntimeEventsAsync` persists a `coder_text_chunk` row
- **THEN** `ITranscriptEventPublisher.PublishAsync` is invoked with a `TranscriptEnvelope` carrying the same payload
- **AND** a connection that subscribed to the transcript type via `SetSubscriptionsAsync` receives it through the new `OnTranscriptEvent` SignalR method

#### Scenario: Transcript events are filtered by subscription set
- **WHEN** a connection has NOT subscribed to `coder_text_chunk`
- **AND** `AppendRuntimeEventsAsync` appends a `coder_text_chunk` row
- **THEN** that connection SHALL NOT receive the `OnTranscriptEvent` for that row

#### Scenario: Transcript events are not on the domain bus
- **WHEN** a `coder_text_chunk` row is appended
- **THEN** `IEventPublisher` is NOT called with that event
- **AND** the `EventBridge` does not forward it to subscribers as a domain event
- **AND** the same applies to `coder_thought_chunk`, `coder_tool_call`, `ralph_task_update`, `ralph_loop_progress`, `agent_liveness_status`, `agent_usage_update`, `agent_session_model_resolved`

#### Scenario: Lifecycle events use the domain bus
- **WHEN** `agent_session_terminal` is appended with a terminal status
- **THEN** `IEventPublisher` publishes the corresponding `com.mohist.agent-session.*` event
- **AND** `ITranscriptEventPublisher` is NOT used for the lifecycle transition
- **AND** `EventBridge` forwards the lifecycle event to subscribed connections on the domain channel

### Requirement: ActivityPage Waiting list receives waiting issues from the server

`AgentRoutes.MapGet("/activity", ...)` SHALL build the `waiting` array from the same set of "issue currently paused on an approval gate" the Web polls in its 5-second fallback and pass it to `AgentSessionQuerier.GetActivityAsync` so that the `Waiting` section in `ActivityPage` populates. The querier SHALL accept that array and the DTO shape SHALL NOT change.

#### Scenario: Waiting issues appear in the activity response
- **WHEN** the Web calls `GET /api/projects/{ref}/agent/activity`
- **AND** the project has an `InProgress` issue paused on an approval gate
- **THEN** the response's `waiting` array contains an entry for that issue
- **AND** the `ActivityPage` Waiting section displays that entry

#### Scenario: Empty waiting section
- **WHEN** no issue in the project is paused on an approval gate
- **THEN** the response's `waiting` array is empty
- **AND** the `ActivityPage` Waiting section shows the empty state message

### Requirement: 5-second polls remain as a fallback

The 5-second `agent/activity`, `agent/status`, and `workflow/status` polls SHALL NOT be removed. They remain the safety net for SignalR being unavailable, the subscription set still being negotiated, or any case where the realtime push has not yet caught up.

#### Scenario: Polls still fire on schedule
- **WHEN** the Web is running with an active SignalR connection
- **THEN** the 5-second polls for `agent/activity`, `agent/status`, and `workflow/status` continue to run
- **AND** they SHALL remain the source of truth when SignalR is disconnected
