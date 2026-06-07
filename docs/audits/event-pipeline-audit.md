# Event Pipeline Audit

> Scope: end-to-end event flow from .NET backend (Grains → `IEventBus` → `EventBridge` → SignalR `/hubs/events`) to TypeScript Web UI (`useEventsConnection` → `LiveTaskProvider` → per-feature hooks).
>
> Audit method: read every `_eventBus.Emit` call site; every `bus.On` registration; every `OnEvent` handler in the web; every `onAgentEvent` / `dispatchAgentEvent` / `onRebaseEvent` / `useEventsConnection` use; every test that asserts a bus delivery.

---

## 1. Domain event sources (in code)

All `_eventBus.Emit(...)` call sites in production code. Nine call sites total. None of the runner, issue, or integration services emit on the bus — they write to the DB only.

| Source file:line | Bus event name | Payload type | Trigger condition |
|---|---|---|---|
| `packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs:892` | `"stage_changed"` | `StageChangedEvent` (record, `IProjectScoped`) | `EmitStageChanged(action, reason?)` is called from `OnWorkflowStartedAsync` (action `"started"`), `OnWorkflowResumedAsync` (`"resumed"`), `OnWorkflowPausedAsync` (`"paused"`), `OnWorkflowStoppedAsync` (`"stopped"`), `OnApprovalApprovedAsync` (`"approved"`), `OnApprovalRejectedAsync` (`"rejected"`). It is the only emitter in the entire workflow domain that goes through a *registered* bus name. |
| `packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:107` | **`dto.Type`** (C# class name) | `WorkflowDomainEventDto` (the DB row) | Inside the `Publish` helper, called from the second `SaveAsync(run, events)` overload (line 53), which is the only path `WorkflowGrain.SaveRunAsync(events)` uses. Every workflow lifecycle event reaches the bus here: `WorkflowRunStarted`, `WorkflowRunResumed`, `WorkflowRunPaused`, `WorkflowRunStopped`, `WorkflowRunFailed`, `WorkflowRunCompleted`, `StageStarted`, `StageCompleted`, `StageFailed`, `StageApprovalRequested`, `StageApprovalResolved`, `TaskCompleted`, `TaskFailed`, `CheckPassed`, `CheckFailed`, `CheckPending`, `RepairScheduled`. None of these names are in `EventBusEventTypes.All`, so the bus has no handler and they are silently dropped (see §5). |
| `packages/server/src/Mohist.Server/Infrastructure/Data/Events/EventStore.cs:26` | **`dto.Type`** (C# class name) | `WorkflowDomainEventDto` | Called from `AppendWorkflowEventAsync`. **This method is unreachable in production code** — only tests call it (`tests/.../EventStoreSpecs.cs`, `tests/.../Support/RecordingEventStore.cs`, `tests/.../Support/EventStoreTestExtensions.cs`). When it *is* called in tests, the same class-name mismatch applies. |
| `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:284` | `"coder_session_started"` | `CoderSessionStartedEvent` (record, `IProjectScoped`) | `EmitStartedAsync` after `AttachAgent` succeeds and produces at least one `AgentSessionEvent`. Fires once per agent process start. |
| `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:301` | `"coder_text_chunk"` | `CoderTranscriptEntryEvent` (record, `IProjectScoped`) | For every `AgentSessionRuntimeEventRow` of type `agent_message_chunk` or `agent_output_chunk`. |
| `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:311` | `"coder_thought_chunk"` | `CoderTranscriptEntryEvent` (record, `IProjectScoped`) | For every runtime event of type `agent_thought_chunk`. |
| `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:324` | `"coder_tool_call"` | `CoderToolCallEvent` (record, `IProjectScoped`) | For every runtime event of type `tool_call` or `tool_call_update` (and only when `toolCallId` is non-empty). |
| `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:345` | `"coder_session_status_changed"` | `CoderSessionStatusChangedEvent` (record, `IProjectScoped`) | `EmitStatusChanged` when the liveness event arrives (`statusChanged = entries.Any(r => r.Type == "agent_liveness_status")`). |
| `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:354` | `"coder_session_completed"` / `"coder_session_failed"` / `"coder_session_cancelled"` | `CoderSessionTerminalEvent` (record, `IProjectScoped`) | `EmitTerminal` when the session is in a terminal phase, OR on direct `FailIfRunningAsync` failure. The terminal string is one of `"completed"`/`"failed"`/`"cancelled"`; the switch maps it to a bus name. |

**Total unique bus names actually emitted by production code: 8.** Six of them are session-related; the other one (`"stage_changed"`) is the only workflow-domain name that crosses the bridge end-to-end. All other "workflow" events travel under their C# class names (17 distinct class names: `WorkflowRunStarted`, `WorkflowRunResumed`, `WorkflowRunPaused`, `WorkflowRunStopped`, `WorkflowRunFailed`, `WorkflowRunCompleted`, `StageStarted`, `StageCompleted`, `StageFailed`, `StageApprovalRequested`, `StageApprovalResolved`, `TaskCompleted`, `TaskFailed`, `CheckPassed`, `CheckFailed`, `CheckPending`, `RepairScheduled`).

---

## 2. EventBus registered types

`packages/server/src/Mohist.Server/Infrastructure/Events/EventBusEventTypes.cs:5-52` declares the array that `EventBridge` subscribes to. Below, for each entry, I list (a) what it's supposed to mean, (b) whether any source actually emits under that bus name (not under a class name).

| # | Bus name | Declared meaning (from web listener contract or implied) | Source emit count | Subscriber count (bridge) |
|---|---|---|---|---|
| 1 | `stage_changed` | `StageChangedEvent` — high-level workflow lifecycle transition | **1** (WorkflowGrain:892) | 1 (EventBridge:34) |
| 2 | `comment_added` | Web expects `{ issueId, projectId, commentId, body, createdAt }` (entities/issue/@x/events.ts:5) | **0** — `IssueGrain.AddCommentAsync` writes to `IssueComments` table (IssueGrain.cs:385-389) but never emits | 1 (dead) |
| 3 | `agent_started` | Web expects `{ issueId, projectId }` | **0** — no emitter anywhere | 1 (dead) |
| 4 | `agent_completed` | Web expects `{ issueId, projectId }` | **0** | 1 (dead) |
| 5 | `agent_paused` | Web expects `{ issueId, projectId }` | **0** | 1 (dead) |
| 6 | `agent_error` | Web expects `{ issueId, projectId, error }` | **0** | 1 (dead) |
| 7 | `approval_requested` | Web expects `{ issueId, projectId, stage }` | **0** — workflow emits `StageApprovalRequested` (class name) but never a bus string `approval_requested` | 1 (dead) |
| 8 | `tool_call` | Runtime event type stored in `AgentSessionRuntimeEventRow.Type` (not a bus name) | **0** (no bus emitter; `AgentSessionGrain.cs:184` and `:188` only query the runtime event type, never `_eventBus.Emit`) | 1 (dead) |
| 9 | `agent_text_chunk` | Not consumed by the Web (no listener in any `onAgentEvent` call) | **0** | 1 (dead) |
| 10 | `main_tool_call` | Web type defined; not consumed | **0** | 1 (dead) |
| 11 | `coder_text_chunk` | Transcript chunk (per session) | **1** (AgentSessionGrain:301) | 1 |
| 12 | `coder_thought_chunk` | Thought chunk (per session) | **1** (AgentSessionGrain:311) | 1 |
| 13 | `coder_tool_call` | Tool call lifecycle (per session) | **1** (AgentSessionGrain:324) | 1 |
| 14 | `plan_session_update` | Plan round transcript update | **0** | 1 (dead) |
| 15 | `merge_queued` | Web expects `{ issueId, projectId, issueNumber, position }` | **0** | 1 (dead) |
| 16 | `merge_started` | Web expects `{ issueId, projectId, issueNumber }` | **0** | 1 (dead) |
| 17 | `merge_completed` | Web expects `{ issueId, projectId, issueNumber }`; consumer shows success toast | **0** | 1 (dead) |
| 18 | `merge_failed` | Web expects `{ issueId, projectId, issueNumber, reason }`; consumer shows error toast | **0** | 1 (dead) |
| 19 | `merge_blocked` | Web has no listener | **0** | 1 (dead) |
| 20 | `agent_conflict_resolution_started` | Web expects `{ issueId, projectId, issueNumber }` | **0** | 1 (dead) |
| 21 | `agent_conflict_resolution_completed` | Web expects `{ issueId, projectId, issueNumber }` | **0** | 1 (dead) |
| 22 | `agent_conflict_resolution_failed` | Web expects `{ issueId, projectId, issueNumber, error }` | **0** | 1 (dead) |
| 23 | `coder_recovery_status` | Session-level recovery; consumed in useSessionTimeline and useSessionTranscript | **0** — runtime event queries use this as a runtime-type string (AgentSessionGrain.cs:319, etc.), never a bus emit | 1 (dead for live flow) |
| 24 | `coder_session_started` | Session-start (per session) | **1** (AgentSessionGrain:284) | 1 |
| 25 | `coder_session_completed` | Session terminal (success) | **1** (AgentSessionGrain:354, default branch) | 1 |
| 26 | `coder_session_failed` | Session terminal (failure) | **1** (AgentSessionGrain:354, "failed" branch) | 1 |
| 27 | `coder_session_cancelled` | Session terminal (cancel) | **1** (AgentSessionGrain:354, "cancelled" branch) | 1 |
| 28 | `coder_session_status_changed` | Liveness / status flip (per session) | **1** (AgentSessionGrain:345) | 1 |
| 29 | `rebase_started` | Web expects `{ issueId, projectId, issueNumber }`; consumed via LiveTaskProvider (sets `rebaseConflict` to null) **and** via the dead `onRebaseEvent` path (see §4) | **0** | 1 (dead) |
| 30 | `rebase_progress` | Web expects `{ issueId, projectId, issueNumber, step }`; consumed via `onRebaseEvent` (dead dispatcher, see §4) | **0** | 1 (dead) |
| 31 | `rebase_completed` | Web expects `{ issueId, projectId, issueNumber, rebased }`; consumed in LiveTaskProvider and `onRebaseEvent` | **0** | 1 (dead) |
| 32 | `rebase_conflict` | Web expects `{ issueId, projectId, issueNumber, conflicts[], status?, error? }`; consumed in LiveTaskProvider and `onRebaseEvent` | **0** | 1 (dead) |
| 33 | `schedule_triggered` | Web has no listener | **0** | 1 (dead) |
| 34 | `schedule_completed` | Web has no listener | **0** | 1 (dead) |
| 35 | `schedule_failed` | Web has no listener | **0** | 1 (dead) |
| 36 | `stage_task_update` | Web expects `{ issueId, projectId, stage, taskId, taskTitle, status, attempt, artifacts[] }`; consumed in LiveTaskProvider + WorkflowView | **0** — name is declared in `AGENT_DETAIL_EVENTS` (entities/agent/model/events.ts:46) but no emitter exists | 1 (dead) |
| 37 | `integration_started` | Web expects `{ issueId, projectId, issueNumber }`; no listener found | **0** | 1 (dead) |
| 38 | `integration_completed` | Web expects `{ issueId, projectId, issueNumber, steps[] }`; no listener found | **0** | 1 (dead) |
| 39 | `integration_failed` | Web expects `{ issueId, projectId, issueNumber, failingStep, error, output }`; no listener found | **0** | 1 (dead) |
| 40 | `integration_preflight_refreshed` | Web has no listener | **0** | 1 (dead) |
| 41 | `ralph_task_update` | Web expects `{ issueId, projectId, executionId, taskId, taskIndex, totalTasks, status, attempt?, error? }`; consumed in LiveTaskProvider + useSessionTimeline | **0** | 1 (dead) |
| 42 | `ralph_loop_progress` | Web expects `{ issueId, projectId, executionId, completed, failed, total }`; consumed in LiveTaskProvider + useSessionTimeline | **0** | 1 (dead) |
| 43 | `plan_round_start` | Web expects `PlanRoundStartEvent`; consumed in useSessionTimeline | **0** | 1 (dead) |
| 44 | `integration_step_updated` | Web expects `{ issueId, projectId, issueNumber, step, status, summary?, output? }`; no listener found | **0** | 1 (dead) |
| 45 | `agent_usage_update` | Web type defined; consumed in LiveTaskProvider + useCoderSessions | **1** (only via the runtime-event loop in AgentSessionGrain.cs:153, which queries the row type — **but** `AgentSessionGrain` has no `_eventBus.Emit("agent_usage_update", ...)` call) | 1 (dead) |

> Caveat for `agent_usage_update` and `coder_recovery_status`: these strings are *only* used as runtime-event `Type` filters in the DB layer. The bus would still need its own emitter to actually push live updates; none exists. The web's `onAgentEvent('agent_usage_update', ...)` and `onAgentEvent('coder_recovery_status', ...)` registrations in `useSessionTimeline.ts:543`, `useSessionTranscript.ts:979`, `useCoderSessions.ts:131` will never fire in production.

**Summary**: 8 of the 45 registered names are emitted; 37 are dead-registered. 9 bus names that the Web does not register (`agent_text_chunk`, `main_tool_call`, `agent_paused`, etc., listed above) are subscribed by the bridge but neither emitted nor listened for.

---

## 3. Bridge (SignalR) consumers

`packages/server/src/Mohist.Server/Events/Hub/EventBridge.cs:24-39` is the only bridge between bus and SignalR. For every name in `EventBusEventTypes.All` it does the same thing:

```csharp
Action<object> handler = data =>
{
    var projectId = ExtractProjectId(data);
    var group = $"project:{projectId ?? "global"}";
    _ = _hub.Clients.Group(group).OnEvent(eventType, data);
};
_bus.On(eventType, handler);
```

Each handler calls `IEventsClient.OnEvent(eventName, data)` on the SignalR group `project:<id>` (or `project:global` if no project id is extractable). The payload is forwarded as-is.

`ExtractProjectId` (`EventBridge.cs:52-68`) checks:
1. `IProjectScoped.ProjectId` (preferred — used by all Coder* events and `StageChangedEvent`).
2. JSON-looks for `projectId` (camelCase) then `ProjectId` (PascalCase).

For a `WorkflowDomainEventDto` payload (the class-name events from `WorkflowRunStore.Publish` / `EventStore.AppendWorkflowEventAsync`), neither branch matches: `WorkflowDomainEventDto` does not implement `IProjectScoped`, and its `Data` field is a discriminated `WorkflowEvent` (no top-level `ProjectId`). So *if* the bridge subscribed to those class names, the payload would route to `project:global`. **The bridge does not subscribe to them today.**

The SignalR contract on the client side is `HubConnection.on('OnEvent', (eventName, data) => ...)` (`shared/api/events-hub.ts:30`). There is no per-event payload filter or per-event method on the hub.

---

## 4. Frontend consumers

There is **only one SignalR entry point**: `useEventsConnection(projectId, onEvent)` in `shared/api/events-hub.ts:19-43`. It calls `onEvent(eventName, data)` for every received `OnEvent`. The single live wiring is `app/providers/LiveTaskProvider.tsx:268` (`useEventsConnection(projectId, handleEvent)`).

### 4.1 `handleEvent` in `LiveTaskProvider.tsx:64-264`

For every event, the handler does (in order):

1. If `eventName` is in `AGENT_DETAIL_EVENTS` (`entities/agent/model/events.ts:25-48`, 23 names), call `dispatchAgentEvent(eventName, parsed)` (LiveTaskProvider:69-71). This republishes the event to a **separate browser `EventTarget`** in `entities/agent/model/events.ts:5`, so per-widget subscribers can `onAgentEvent(name, handler)` without re-routing through the bus.
2. Hard-coded `ralph_task_update` block (LiveTaskProvider:73-90) — updates `activeTaskId` / `activeTaskElapsedMs` for the live task timer.
3. Hard-coded query-invalidation block (LiveTaskProvider:92-106) — invalidates `['agent-activity']` for 11 names: `coder_text_chunk`, `coder_tool_call`, `ralph_task_update`, `ralph_loop_progress`, `coder_session_started`, `coder_session_completed`, `coder_session_failed`, `coder_session_cancelled`, `coder_session_status_changed`, `agent_liveness_status`, `agent_usage_update`. (`agent_liveness_status` and `agent_usage_update` are in `AGENT_DETAIL_EVENTS` but NOT in `EventBusEventTypes.All` — so even if the bridge forwarded them, the bus would never see them; the invalidation is a no-op for any name that has no emitter.)
4. A `switch (eventName as EventName)` covering the **non-agent-detail** names (LiveTaskProvider:108-260). Table below.

| Event name (bus string) | File:line | State mutated |
|---|---|---|
| `stage_changed` | `LiveTaskProvider.tsx:109-112` | `invalidateQueries(['issues'])` (payload not used; declared contract `{ issueId, projectId, from, to }` in `entities/issue/@x/events.ts:4` is **never matched by the real payload** — see §5/§8) |
| `comment_added` | `LiveTaskProvider.tsx:113-120` | `invalidateQueries(['issues'])` and `['issues', 'detail', issueId]` (cast on `parsed.issueId`) |
| `agent_started` | `LiveTaskProvider.tsx:121-128` | `invalidateQueries(['agent-status', 'agent-activity', 'issues'])` |
| `agent_completed` | `LiveTaskProvider.tsx:121-128` | (same as above) |
| `agent_paused` | `LiveTaskProvider.tsx:121-150` | invalidations + toast.info(`Issue #${n} needs approval`) if not currently viewing that issue. Cast: `EventMap['agent_paused']` |
| `agent_error` | `LiveTaskProvider.tsx:121-150` | invalidations + toast.error(`Issue #${n} encountered an error`). Cast: `EventMap['agent_error']` |
| `agent_blocked` | `LiveTaskProvider.tsx:152-157` | `invalidateQueries(['issues', 'agent-status', 'agent-activity'])` |
| `approval_requested` | `LiveTaskProvider.tsx:158-162` | `invalidateQueries(['issues', 'agent-activity'])` |
| `merge_queued` | `LiveTaskProvider.tsx:163-176` | `invalidateQueries(['issues'])` |
| `merge_started` | `LiveTaskProvider.tsx:163-176` | `invalidateQueries(['issues'])` |
| `merge_completed` | `LiveTaskProvider.tsx:163-176` | `invalidateQueries(['issues'])` + toast.success(`Issue #${n} merged successfully`) |
| `merge_failed` | `LiveTaskProvider.tsx:163-176` | `invalidateQueries(['issues'])` + toast.error(`Merge failed for Issue #${n}`) |
| `rebase_completed` | `LiveTaskProvider.tsx:177-181` | `setRebaseConflict(null)` + `invalidateQueries(['issues'])` |
| `rebase_conflict` | `LiveTaskProvider.tsx:182-194` | `setRebaseConflict({ issueNumber, conflicts, status, error? })` + toast.error on `status === 'failed'` + invalidations |
| `agent_conflict_resolution_started` | `LiveTaskProvider.tsx:195-204` | `setRebaseConflict(prev => ({ ...prev, status: 'resolving' }))` (only if same issue) + invalidations |
| `agent_conflict_resolution_completed` | `LiveTaskProvider.tsx:205-214` | (same pattern) |
| `agent_conflict_resolution_failed` | `LiveTaskProvider.tsx:215-224` | `setRebaseConflict(prev => ({ ...prev, status: 'failed', error }))` + invalidations |
| `check_started` | `LiveTaskProvider.tsx:225-230` | `invalidateQueries(['issues'])` |
| `check_update` | `LiveTaskProvider.tsx:225-230` | `invalidateQueries(['issues'])` |
| `check_suite_status_changed` | `LiveTaskProvider.tsx:225-230` | `invalidateQueries(['issues'])` |
| `stage_task_update` | `LiveTaskProvider.tsx:231-235` | `invalidateQueries(['issues', 'agent-activity'])` |
| `base_drift_detected` | `LiveTaskProvider.tsx:236-249` | invalidations + toast.warning(`Issue #${n} needs attention before continuing`) when `decision === 'needs-attention'` |
| `rebase_opportunity` | `LiveTaskProvider.tsx:236-249` | invalidations (toast suppressed) |
| `user_attention_requested` | `LiveTaskProvider.tsx:251-259` | invalidations + toast.info(`Issue #${n}: ${reason}`) |

There is **no `default` arm in the switch.** Any event name not listed above is silently dropped at the Web after `dispatchAgentEvent` runs.

### 4.2 `dispatchAgentEvent` / `onAgentEvent` (per-widget subs)

`entities/agent/model/events.ts:7-23` defines a single browser `EventTarget` for the 23 names in `AGENT_DETAIL_EVENTS`. Subscribers:

| File:line | Subscribed names |
|---|---|
| `entities/coder-session/model/useCoderSessions.ts:65, 90, 106, 131` | `coder_session_started`, `coder_session_completed`, `coder_session_status_changed`, `agent_usage_update` |
| `widgets/coder-session/model/useSessionTimeline.ts:325, 375, 389, 437, 453, 504, 531, 543, 585` | `plan_round_start`, `plan_session_update`, `plan_round_complete`, `coder_text_chunk`, `coder_tool_call`, `ralph_task_update`, `ralph_loop_progress`, `coder_recovery_status`, `agent_liveness_status` |
| `widgets/session-transcript/model/useSessionTranscript.ts:729, 746, 765, 880, 901, 940, 979, 1014` | `coder_text_chunk`, `coder_thought_chunk`, `coder_tool_call`, `coder_session_completed`, `coder_session_failed`, `coder_session_cancelled`, `coder_recovery_status`, `agent_liveness_status` |
| `widgets/issue-workflow/ui/WorkflowView.tsx:1161` | `stage_task_update` |

Of these 23 names, the bus has 6 emitters (`coder_session_started`, `coder_text_chunk`, `coder_thought_chunk`, `coder_tool_call`, `coder_session_status_changed`, `coder_session_completed`/`_failed`/`_cancelled`). The remaining 16 are dead (see §2).

### 4.3 `onRebaseEvent` (the orphan pattern)

`entities/issue/model/rebase-events.ts:13-19` exposes an `EventTarget` named `rebase-event`. `WorktreePanel.tsx:44-66` subscribes via `onRebaseEvent`. The dispatcher `dispatchRebaseEvent` (line 9-11) is **exported from `entities/issue/index.ts:4` and is never called anywhere in `packages/web/src`** (verified by grep). The actual `rebase_completed` / `rebase_conflict` flow that drives the UI goes through `LiveTaskProvider.setRebaseConflict` (see 4.1), not through `WorktreePanel`'s `onRebaseEvent` subscription. The `onRebaseEvent` handler in `WorktreePanel` is dead code in production; only `useLiveTask().rebaseConflict` (from `LiveTaskContext`) is observable, and that path is fed by `LiveTaskProvider` directly.

Note: there is no `useLiveTask().rebaseStep` (a rebase-step string). `WorktreePanel.tsx:41` initializes `rebaseStep` to `null` and tries to set it from `onRebaseEvent('rebase_progress', ...)` — which never fires. The rebase step UI is dead.

---

## 5. The dead/mismatched zones

### 5.1 The 37 dead bus registrations (re-state for emphasis)

Names registered in `EventBusEventTypes.All` with **0 emitters** in production code:
`comment_added`, `agent_started`, `agent_completed`, `agent_paused`, `agent_error`, `approval_requested`, `tool_call`, `agent_text_chunk`, `main_tool_call`, `plan_session_update`, `merge_queued`, `merge_started`, `merge_completed`, `merge_failed`, `merge_blocked`, `agent_conflict_resolution_started`, `agent_conflict_resolution_completed`, `agent_conflict_resolution_failed`, `coder_recovery_status`, `rebase_started`, `rebase_progress`, `rebase_completed`, `rebase_conflict`, `schedule_triggered`, `schedule_completed`, `schedule_failed`, `stage_task_update`, `integration_started`, `integration_completed`, `integration_failed`, `integration_preflight_refreshed`, `ralph_task_update`, `ralph_loop_progress`, `plan_round_start`, `integration_step_updated`, `agent_usage_update`, plus the 4 names that the Web does not register as live events: `agent_text_chunk`, `main_tool_call`, `agent_paused`, `plan_session_update`, `merge_blocked`, `agent_conflict_resolution_*` (4), `coder_recovery_status`, `rebase_started`/`rebase_progress`/`rebase_completed`/`rebase_conflict` (4), `schedule_*` (3), `integration_*` (4), `plan_round_start`, `integration_step_updated`, `agent_usage_update`. All subscribed by `EventBridge`; all dead.

### 5.2 The "silently dropped" class-name events

`WorkflowRunStore.Publish` (`packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:102-109`) calls `_eventBus.Emit(dto.Type, dto)` where `dto.Type` is set by `WorkflowEventSerializer.Type(payload)` (`Infrastructure/Events/WorkflowEventSerializer.cs:10`) to `Unwrap(payload).GetType().Name`. For the `WorkflowEvent` union (`Workflow/Domain/Run/WorkflowEvent.cs:3-20`), the emitted names are exactly:

```
WorkflowRunStarted, WorkflowRunResumed, WorkflowRunPaused, WorkflowRunStopped,
WorkflowRunCompleted, WorkflowRunFailed,
StageStarted, StageCompleted, StageFailed,
StageApprovalRequested, StageApprovalResolved,
TaskCompleted, TaskFailed,
CheckPassed, CheckFailed, CheckPending, RepairScheduled
```

Seventeen distinct class names. **None of them is in `EventBusEventTypes.All` (line 5-52).** The bus stores handlers in a `ConcurrentDictionary<string, List<Action<object>>>` (`InMemoryEventBus.cs:7`), and `Emit` (line 35-56) silently no-ops when no handler is registered. So the entire workflow domain event stream — every `StageStarted`, every `TaskCompleted`, every `StageApprovalRequested`, every `WorkflowRunFailed` — is emitted onto the bus and dropped on the floor. The DB write succeeds (which is what every test asserts), and the bridge never sees the events.

The same applies to `EventStore.AppendWorkflowEventAsync` (`Infrastructure/Data/Events/EventStore.cs:19-28`) — also emits with `dto.Type`. This path is not called from production code today, but if it ever is, the events will likewise be dropped.

### 5.3 Payload-shape mismatches

These are mismatches between the declared/expected payload shape and what the source actually sends. (Note: JSON property names are case-sensitive in `System.Text.Json` unless `PropertyNameCaseInsensitive` is enabled on the *deserializer*. The bus forwards payloads as `object`, so the bridge never re-deserializes them; the SignalR client receives the same object that the emitter sent. If the receiver deserializes, casing matters.)

| Bus name | Declared Web shape (`entities/issue/@x/events.ts`) | Actual emitted shape | Mismatch |
|---|---|---|---|
| `stage_changed` | `{ issueId, projectId, from, to }` | `StageChangedEvent`: `{ projectId, workflowRunId, stage, status, action, reason, timestamp }` | **No `issueId`, no `from`, no `to`** in the actual payload. The `from`/`to` field shape is impossible to derive from `StageChangedEvent`. The Web also does not destructure these fields (LiveTaskProvider:109-112 only invalidates), so the mismatch is latent — but if the Web ever tries to read `evt.issueId` it will be `undefined`. |
| `coder_session_started` | `{ issueId, projectId, coderSessionId, acpSessionId, executionId?, model?, coderType?, stage?, taskDescription?, title? }` | `CoderSessionStartedEvent`: `{ issueId, projectId, coderSessionId, acpSessionId, executionId?, model?, stage?, taskDescription?, title? }` | **Missing `coderType` field in the source.** `useCoderSessions.ts:75` reads `detail.coderType` and will receive `undefined`. |
| `coder_text_chunk` | `{ issueId, projectId, executionId, acpSessionId, text, coderSessionId?, model? }` | `CoderTranscriptEntryEvent`: `{ issueId, projectId, executionId?, acpSessionId, coderSessionId, text? }` | **`executionId` is nullable in source but non-nullable in Web type.** The Web reads `detail.executionId` and may receive `undefined` even though the type says `string`. Likewise `model?` is in the Web type but absent from the source — the bus never sends it for `coder_text_chunk`. |
| `coder_thought_chunk` | (same as `coder_text_chunk`) | `CoderTranscriptEntryEvent` (same as `coder_text_chunk`) | same |
| `coder_tool_call` | Web type includes `coderSessionId`, `model`, `rawOutputMetadata`, `displayTitle`, `displaySubtitle`, `category` | `CoderToolCallEvent` has `coderSessionId`, `model`-ish fields are **absent** (no `model` in the source record at all), `rawOutputMetadata` is **absent** (the source has `metadata` and `details`, not `rawOutputMetadata`), `displayTitle`, `displaySubtitle`, `category` are present | `model` and `rawOutputMetadata` are declared on the Web type but never sent. Web consumer at `useSessionTimeline.ts:469` and `:485` will receive `undefined`. |
| `coder_session_status_changed` | Web type includes `probeSentAt?`, `probeDeadlineAt?` | `CoderSessionStatusChangedEvent` has `lastDataAt?`, `failureReason?` (no probe fields) | `probeSentAt` / `probeDeadlineAt` declared but never sent. |
| `coder_session_completed` | Web: `{ issueId, projectId, coderSessionId, status: 'completed' \| 'failed', duration }` | `CoderSessionTerminalEvent`: `{ issueId, projectId, coderSessionId, status, reason?, duration }` | `reason` is absent from the Web type but present in the source. Web consumer `useCoderSessions.ts:90-103` reads `detail.status` and `detail.coderSessionId` only — no `reason` access — so this is also latent. |
| `coder_session_failed` | Web: `{ issueId, projectId, coderSessionId, reason? }` | `CoderSessionTerminalEvent` (same as above) | OK on shape, but the Web `useSessionTranscript.ts:901-925` reads `detail.failureReason` (CamelCase) — the source field is `reason`. **Casing mismatch**: `useSessionTranscript.ts:901-925` calls into a consumer that destructures `detail.failureReason` (or `detail.reason`?). I read this from line ~905 onward — the only field read is `coderSessionId` and a fallback on `detail.issueId`; the failure reason is not consumed by name in the surface I read, but the type declares it as `reason?`. |
| `agent_usage_update` | Web type includes `executionId?`, `acpSessionId?`, `coderSessionId?` | The bus would emit whatever shape the source uses — but **no source emits `agent_usage_update`** (the runtime-event loop processes the row type but never calls `_eventBus.Emit`). The Web is wired to a name that has no emitter. | N/A (no emitter) |
| `coder_session_started` (re-stated) | — | source uses `AcpSessionId` (PascalCase via `[property: JsonPropertyName("acpSessionId")]`); Web type uses `acpSessionId` | **OK** because the `[JsonPropertyName]` attribute forces camelCase on serialization. The other PascalCase vs camelCase risk is on internal records that don't use the attribute. |

For all *class-name* workflow events (the 17 names in §5.2), there is **no declared Web shape at all** — `EventMap` in `entities/issue/@x/events.ts:3-33` does not include them. The closest the Web comes is `useEventsConnection` callbacks that listen for `StageStarted`-like names — there are none.

### 5.4 The `dispatchRebaseEvent` orphan

`entities/issue/model/rebase-events.ts:9-11` exports `dispatchRebaseEvent` and `entities/issue/index.ts:4` re-exports it. No file under `packages/web/src` calls it. The `WorktreePanel.tsx:44-66` subscriber via `onRebaseEvent` is never invoked. (The rebase UI in `WorktreePanel` is wired to `useLiveTask().rebaseConflict` from `LiveTaskContext`, not to the EventTarget.)

---

## 6. The bus name vs class name split

### 6.1 The two ways to resolve this

**(A) Add the 17 class names to `EventBusEventTypes.All`.** Pros: zero emitter-side changes; the existing `WorkflowRunStore.Publish` / `EventStore.AppendWorkflowEventAsync` paths start flowing to the bridge immediately. The `WorkflowEventSerializer.Type` method already returns the right strings. Cons: the names are PascalCase (`StageStarted`, `WorkflowRunFailed`) and inconsistent with the snake_case used by every other bus name. Casing across the wire is fine, but the Web would have to switch on `case 'StageStarted':` etc. — which clashes with the existing snake_case in `EventMap`. Also: the bridge will forward these to the project group, but the workflow domain doesn't carry `ProjectId` on the wire today (it has `workflowRunId` and the data is the `WorkflowEvent`), so `ExtractProjectId` falls through to `project:global`. **All clients would receive every workflow event** (no project filtering).

**(B) Have the emitter rename the class name to a bus-registered string.** That means `WorkflowRunStore.Publish` and `EventStore.AppendWorkflowEventAsync` should use a new function `WorkflowEventBusName(WorkflowEvent)` that maps each C# type to a snake_case bus name. Suggested mapping (aligned with the 45 existing names where possible):

| C# class | Suggested bus name | Web `EventMap` already declared? |
|---|---|---|
| `WorkflowRunStarted` | `workflow_run_started` | No |
| `WorkflowRunResumed` | `workflow_run_resumed` | No |
| `WorkflowRunPaused` | `workflow_run_paused` | No |
| `WorkflowRunStopped` | `workflow_run_stopped` | No |
| `WorkflowRunCompleted` | `workflow_run_completed` | No |
| `WorkflowRunFailed` | `workflow_run_failed` | No |
| `StageStarted` | `stage_started` | No (the Web already has `stage_changed`, not `stage_started`) |
| `StageCompleted` | `stage_completed` | No |
| `StageFailed` | `stage_failed` | No |
| `StageApprovalRequested` | `approval_requested` | **Yes** — `entities/issue/@x/events.ts:11` already declares `approval_requested: { issueId, projectId, stage }`. This is the obvious place to start, because the registration and the Web type are both there. |
| `StageApprovalResolved` (approved) | `approval_resolved` (with payload shape `{ issueId, projectId, stage, result, reason? }`) | No (must be added) |
| `TaskCompleted` | `task_completed` (or `stage_task_update` with `status: 'completed'`) | No |
| `TaskFailed` | `task_failed` | No |
| `CheckPassed` | `check_update` (with `status: 'pass'`) | **Yes** — `check_update` is registered; Web type already exists |
| `CheckFailed` | `check_update` (with `status: 'fail'`) | Yes |
| `CheckPending` | `check_update` (with `status: 'pending'`) | Yes |
| `RepairScheduled` | `repair_scheduled` | No |

Pros: clean separation of domain class names (used in DB, used internally) from bus wire names (snake_case, contract-driven, registered in `EventBusEventTypes.All`). The Web's `EventMap` and `AGENT_DETAIL_EVENTS` are the source of truth for live events. Cons: more code to write; the mapping must be kept in sync with new domain events; existing tests would need updating.

**Recommendation: (B).** The bus registration is already the wire contract; the class name is an internal serialization concern. The audit shows that having a single source of truth for "what is live" prevents exactly the class of bug in §5.2. Adopt (A) only as a stopgap if you need a fix in one PR and cannot land the rename.

### 6.2 Implication for `EventBridge` subscription

Under both (A) and (B), the bridge does not need to change — it loops `foreach (var eventType in _eventTypes)` and subscribes each. The change is in `EventBusEventTypes.All` (the registry) and in the emitter-side mapping (if B) or just the registry (if A).

### 6.3 Implication for Web's event-name constants

Under (B), the Web's `entities/issue/@x/events.ts:3-33` (`EventMap`) and `entities/agent/model/events.ts:25-48` (`AGENT_DETAIL_EVENTS`) become the de-facto wire contract. The 17 currently-absent names need to be added to `EventMap` with declared payload shapes that match what the backend will actually send. The current `StageChangedEvent` payload is `{ projectId, workflowRunId, stage, status, action, reason, timestamp }`, not the Web's `{ issueId, projectId, from, to }` — the Web declaration for `stage_changed` is wrong and must be corrected (or the backend must add `issueId` to `StageChangedEvent` and compute `from`/`to` from the prior stage).

### 6.4 Project-scoping for class-name events

The `ExtractProjectId` JSON path looks for `projectId`/`ProjectId` at the top level. `WorkflowDomainEventDto` has neither (its data is the inner `WorkflowEvent`, which is `StageStarted(stage)` etc. with no project). To route these to the project group, the bridge would need to consult a `WorkflowRun -> ProjectId` table. Cheaper: add `ProjectId` to `WorkflowDomainEventDto` (or to the `EventRow` shape that backs it). The `StageChangedEvent` payload already carries `ProjectId` via `IProjectScoped`, so that path works for `stage_changed` today; the class-name events do not.

---

## 7. The "what should be event-driven" list

For each row, "current path" is what exists today in `packages/server/src/Mohist.Server`; "event-driven alternative" is what the existing `EventBusEventTypes.All` and `EventMap` declare; "trade-off" notes the cost of the current approach.

| # | Concern | Current path | Event-driven alternative | Trade-off |
|---|---|---|---|---|
| 1 | Workflow `Completed` → Issue `Done` | `WorkflowGrain.OnWorkflowCompletedAsync` (line 967) calls `DispatchCompletedHooksAsync` (line 995) → `IWorkflowCompletionHook` chain → `IssueWorkflowCompletionHook.OnCompletedAsync` (line 24) → `IssueGrain.CompleteWorkAsync` (IssueGrain.cs:172) → `Issue.Complete` (Issue.Transitions.cs:52) sets `_status = Done`. | Bus name `workflow_run_completed` could carry `(projectId, workflowRunId, issueId, issueNumber)`. The hook pattern works; nothing to fix. | The current hook is the right shape. Bus would just be a notification channel, not a coupling point. |
| 2 | Workflow `Failed` → Issue ??? | `WorkflowGrain.On` (line 928) handles `WorkflowRunFailed` by *disabling* the heartbeat reminder; no hook fires. `IssueWorkflowCompletionHook` is for `Completed` only. There is no `IssueWorkflowFailureHook`. The issue stays `InProgress` indefinitely. | A `IWorkflowFailureHook` interface (parallel to `IWorkflowCompletionHook`) registered in DI; or an `IWorkflowCompletionHook.OnFailedAsync` method. The bus name `workflow_run_failed` is a clean fit. | The grain's `On` switch already special-cases `WorkflowRunFailed` to disable the heartbeat, so the failure is observed at the grain level. The issue not transitioning out of `InProgress` is a product gap: the UI's `LiveTaskProvider` cannot show "issue failed" because nothing emits, and the issue list query keeps showing the issue as in-progress. A user-facing "this issue failed" badge is impossible today. |
| 3 | Workflow `AwaitingApproval` → Web badge | `WorkflowGrain.EmitStageChanged` only fires on `started`/`resumed`/`paused`/`stopped`/`approved`/`rejected` — it does **not** fire on `StageApprovalRequested`. The Web's `LiveTaskProvider` listens for `approval_requested` (line 158) but no source emits it. The Web falls back to `useQuery` polling on `issue.workflowStatus` and `issue.approvalState?.status === 'awaiting'`. | Add `approval_requested` to the bus: emit when `WorkflowGrain.On` handles `StageApprovalRequested` (currently line 933 just disables the heartbeat). | The polling fallback works but has visible latency and reloads full issue objects. A live push would be the right answer. |
| 4 | Workflow `ApprovalResolved(rejected)` → Web toast | `WorkflowGrain.OnApprovalRejectedAsync` (line 989) calls `EmitStageChanged("rejected", reason)`. The Web listens for `stage_changed` and invalidates `['issues']` (LiveTaskProvider:109-112). The "rejected" reason is **not** surfaced as a toast because the switch arm for `stage_changed` does not look at `action === 'rejected'`. There is no `agent_error`-style "Issue #N was rejected" toast. | A dedicated `approval_resolved` event with `{ issueId, projectId, stage, result, reason? }` and a `LiveTaskProvider` arm that does `toast.warning(Issue #${n}: ${reason})` on `result === 'rejected'`. | The current path is "the user can see the rejection in the timeline" but no proactive notification. This is a product gap. |
| 5 | Agent session `Completed` → ??? | `AgentSessionGrain.EmitTerminal(session, "completed")` (line 354) emits `coder_session_completed` on the bus. The bridge forwards; the Web's `useCoderSessions` (line 90) marks the session `status: detail.status, completedAt: now`. The `LiveTaskProvider` invalidates `['agent-activity']` (line 98). There is **no** link from session completion back to the workflow grain: the runner reports `WorkResult.Status == "completed"` to `WorkflowGrain.ReportResultAsync` (WorkflowGrain.cs:358), which is what actually advances the workflow. The bus emit is observability, not coordination. | Keep as is. The session emit is a *side channel* for UI; the workflow advance is via a separate `runner -> grain` report path. This is correct: events are not a coordination mechanism here. | None. The dual paths are intentional and necessary. |
| 6 | Agent session `Failed` → Workflow task marked failed | The session `Failed` is observed only via the bus (`coder_session_failed`) and the DB row update. The workflow task that owns the session is marked failed by the runner's `WorkResult.Status == "failed"` report, which goes through `WorkflowGrain.ReportResultAsync` (line 358) → `ProcessTaskResult` (line 724) → `FailTask` (WorkflowRun.Task.cs:38 → `WorkflowRunFailed` event). These are *two* failure paths: a session can go terminal without a runner report arriving, and the workflow task will hang (still in `Running`) until the heartbeat reminder (5 s due, 1 min period) notices. | Either (a) the session failure emit triggers a separate workflow-grain call to fail the lease, or (b) the runner reports both `agent_liveness_status` and `WorkResult` to the grain; today the grain doesn't know the session died unless the runner tells it. | The current path is fragile. The session emit is observability, but the workflow-grain still needs a `ReportResultAsync("session-failed", ...)` call. A session failure can strand the task until the heartbeat. |
| 7 | Runner `Registered` / `Unregistered` → Web runner panel updates | `RunnerGrain.RegisterAsync` / `UnregisterAsync` (RunnerGrain.cs:51, 74) call into `IRunnerRegistryGrain` but **never emit on the bus**. There is no `runner_registered` / `runner_unregistered` in `EventBusEventTypes.All`. The Web's runner panel updates via TanStack Query polling `useAgentStatus` and the runner endpoints. | Add `runner_registered` / `runner_unregistered` emits from `RunnerGrain` and corresponding Web `case` arms in `LiveTaskProvider` (or a separate provider). | Polling works for the runner count display but doesn't trigger UI affordances (e.g., "this issue's runner just went offline") without a refresh. |
| 8 | Workflow `Stopped` → Issue `Cancelled` | `WorkflowGrain.OnWorkflowStoppedAsync` (line 961) calls `EmitStageChanged("stopped", reason)`. The `IssueWorkflowCompletionHook` is only invoked on `Completed` (via `DispatchCompletedHooksAsync`, line 967). The issue's `CancelAsync` path (IssueGrain.cs:160) is *only* invoked from the API `/api/.../stop` (it stops the workflow with reason "issue-closed" *and* closes the issue). If the workflow is stopped via any other path (e.g., direct `wfGrain.StopAsync` or the grain's own stop logic), the issue stays `InProgress`. | The hook should also fire on `WorkflowRunStopped`. A `IWorkflowCompletionHook.OnStoppedAsync` would be parallel to the `OnCompletedAsync` shape. | A workflow can be stopped today (e.g., timeout, manual stop in runner) without the issue being moved to `Cancelled`. The product consequence is the same as #2. |
| 9 | Stage `Completed` → Web stage bar | No emit. The Web's `StageBar` (rendered in `WorkflowView.tsx`) is fed by `useWorkflowTimeline` polling `GET /api/.../workflow/timeline` or `GET /api/issues/...`. There is no `stage_completed` in the bus registry, and even if there were, no emitter. | Map `StageCompleted` to a bus name and emit from `WorkflowGrain.On` (the switch on line 920-941 is the natural place — currently `StageCompleted` only releases stage locks). | The polling fallback is what works today. |
| 10 | Task `Completed` → Web task list | Same as #9. `TaskCompleted` is in the class-name emissions but dropped at the bus (see §5.2). | Same fix as #9. | Polling fallback. |
| 11 | Check `Passed` / `Failed` → Web check list | Same as #9/#10. The class-name emissions are dropped. The Web's `StepList` shows check status from the polled stage state. | Map `CheckPassed`/`CheckFailed`/`CheckPending` to `check_update` with the appropriate `status` field. `check_update` is **already** a registered bus name with a Web `EventMap` entry (entities/issue/@x/events.ts:24). The fix is purely on the backend: emit `check_update` from `WorkflowGrain.On` when handling `CheckPassed`/`CheckFailed`/`CheckPending` (currently lines 937-939 just call heartbeat or do nothing). | The Web's `case 'check_update'` (LiveTaskProvider:225-230) invalidates the issues query, which is the right behavior — but no event ever fires to trigger that invalidation. |
| 12 | Approval `Resolved(approved)` → Workflow advances | `WorkflowGrain.ApproveAsync` (line 160) is the API. There is **no** `approval_resolved` in the bus registry. The approval is the trigger for workflow advance (via the grain's approve method calling `_run.Approve()`); the advance is not driven by a bus event, it is driven by the API call. | Keep the API call as the trigger; add a bus notification for the UI to see the approval take effect. | The path is correct for coordination. The observability gap is the same as #3/#4. |

---

## 8. The naming and shape contract

Below is what the Web's listeners expect vs what the backend actually emits, derived from `LiveTaskProvider.handleEvent` (app/providers/LiveTaskProvider.tsx:64-264) and the `EventMap` / `AgentDetailEventMap` types (entities/issue/@x/events.ts:3-33, entities/agent/model/types.ts:62-100).

For each event name the Web listens for, I list: (a) the Web's declared `EventMap[K]` shape, (b) the actual emitted shape (if the bus has an emitter), (c) where the consumer reads the payload, and (d) the verdict.

### 8.1 Live events (end-to-end)

#### `stage_changed` — `LiveTaskProvider.tsx:109-112`
- **Web declared**: `{ issueId, projectId, from: string, to: string }` (entities/issue/@x/events.ts:4).
- **Backend emitted**: `StageChangedEvent { projectId, workflowRunId, stage, status, action, reason, timestamp }` (Infrastructure/Events/StageChangedEvent.cs:6-13).
- **Consumer reads**: nothing from the payload — only `invalidateQueries(['issues'])`.
- **Verdict**: **Mismatched shape**, but consumer does not read fields, so it works in practice. If a future consumer does `parsed.issueId`, it will get `undefined`. The `from`/`to` fields declared in the Web type are simply impossible to populate without a server-side change (the server would need to track the previous stage and project it on the event). Either fix the Web type to match the actual payload, or extend `StageChangedEvent` with `issueId` and `fromStage`/`toStage`.

#### `coder_session_started` — `useCoderSessions.ts:65-87`
- **Web declared**: `{ issueId, projectId, coderSessionId, acpSessionId, executionId?, model?, coderType?, stage?, taskDescription?, title? }` (entities/agent/model/types.ts:75).
- **Backend emitted**: `CoderSessionStartedEvent { issueId, projectId, coderSessionId, acpSessionId, executionId?, model?, stage?, taskDescription?, title? }` (Sessions/Domain/Events/CoderSessionEvents.cs:7-16).
- **Consumer reads**: `detail.issueId`, `detail.coderSessionId`, `detail.acpSessionId`, `detail.executionId`, `detail.model`, `detail.stage`, `detail.title` (useCoderSessions.ts:66-78). `detail.coderType` is read at line 76 but the backend never sends it.
- **Verdict**: **Field mismatch on `coderType`**: Web type says `string?`, backend never sends it. `useCoderSessions.ts:76` will always see `undefined`. Either add `coderType` to `CoderSessionStartedEvent` or remove from the Web type. (Other fields read match the source.)

#### `coder_session_completed` — `useCoderSessions.ts:90-103`
- **Web declared**: `{ issueId, projectId, coderSessionId, status: 'completed' | 'failed', duration }` (entities/agent/model/types.ts:76).
- **Backend emitted**: `CoderSessionTerminalEvent { issueId, projectId, coderSessionId, status, reason?, duration }` (Sessions/Domain/Events/CoderSessionEvents.cs:54-60). `status` is the string `"completed"`, `"failed"`, or `"cancelled"`.
- **Consumer reads**: `detail.issueId`, `detail.coderSessionId`, `detail.status` (useCoderSessions.ts:91-96).
- **Verdict**: Type narrows `status` to `'completed' | 'failed'`; backend may send `'cancelled'` for the completion case (no — for cancelled it uses the `coder_session_cancelled` bus name). For `coder_session_completed` the source `status` is always `"completed"`. **OK on shape**, modulo the Web type's `status` union being narrower than reality.

#### `coder_session_failed` — `useSessionTranscript.ts:901-925`
- **Web declared**: `{ issueId, projectId, coderSessionId, reason? }` (entities/agent/model/types.ts:77).
- **Backend emitted**: `CoderSessionTerminalEvent { issueId, projectId, coderSessionId, status, reason?, duration }`. The terminal string is `"failed"`.
- **Consumer reads**: `detail.coderSessionId` (and possibly the full event).
- **Verdict**: OK on shape.

#### `coder_session_cancelled` — `useSessionTranscript.ts:940-960`
- **Web declared**: `{ issueId, projectId, coderSessionId, reason? }` (entities/agent/model/types.ts:78).
- **Backend emitted**: same as above with `status === "cancelled"`.
- **Verdict**: OK.

#### `coder_session_status_changed` — `useCoderSessions.ts:106-128`
- **Web declared**: `{ issueId, projectId, coderSessionId, acpSessionId, status, lastDataAt?, probeSentAt?, probeDeadlineAt?, failureReason? }` (entities/agent/model/types.ts:79).
- **Backend emitted**: `CoderSessionStatusChangedEvent { issueId, projectId, coderSessionId, acpSessionId, status, lastDataAt?, failureReason? }` (CoderSessionEvents.cs:45-52). **No `probeSentAt` or `probeDeadlineAt`.**
- **Consumer reads**: `detail.coderSessionId`, `detail.status`, `detail.lastDataAt`, `detail.failureReason` (useCoderSessions.ts:109-118). `probeSentAt` and `probeDeadlineAt` are also read at lines 116-117 but the backend never sends them.
- **Verdict**: **Field mismatch on `probeSentAt` / `probeDeadlineAt`**: Web type says `string | null`, backend never sends. `useCoderSessions.ts:116-117` will always see `undefined` (the code defensively only sets the field when `!== undefined`, so the UI doesn't break — but the values are always missing).

#### `coder_text_chunk` — `useSessionTimeline.ts:437-449`, `useSessionTranscript.ts:729-744`
- **Web declared**: `{ issueId, projectId, executionId, acpSessionId, text, coderSessionId?, model? }` (entities/agent/model/types.ts:65).
- **Backend emitted**: `CoderTranscriptEntryEvent { issueId, projectId, executionId?, acpSessionId, coderSessionId, text? }` (CoderSessionEvents.cs:18-24). **`executionId` is nullable in source but non-nullable in Web type. `model` is declared on Web type but absent from source.**
- **Consumer reads**: `detail.acpSessionId`, `detail.text` (useSessionTimeline.ts:440-446), and the same in useSessionTranscript. `detail.model` is in the Web type but never used by the consumers I read.
- **Verdict**: **Mismatches are latent**: `executionId` may be `undefined` despite the Web type; `model` is declared but never sent. The consumers I read don't access these fields by name, so the bug is hidden.

#### `coder_thought_chunk` — `useSessionTranscript.ts:746-763`
- **Web declared**: same shape as `coder_text_chunk`.
- **Backend emitted**: same source record (`CoderTranscriptEntryEvent`).
- **Verdict**: same as above.

#### `coder_tool_call` — `useSessionTimeline.ts:453-501`, `useSessionTranscript.ts:765-865`
- **Web declared**: `{ issueId, projectId, executionId, acpSessionId, toolName, state, toolCallId, title?, rawInput?, rawOutput?, rawOutputMetadata?, metadata?, details?, normalizedName?, displayTitle?, displaySubtitle?, category?, coderSessionId?, model? }` (entities/agent/model/types.ts:67).
- **Backend emitted**: `CoderToolCallEvent { issueId, projectId, executionId?, acpSessionId, coderSessionId, toolName, state, toolCallId, title?, rawInput?, rawOutput?, metadata?, details?, normalizedName?, displayTitle?, displaySubtitle?, category? }` (CoderSessionEvents.cs:26-43). **`rawOutputMetadata` is absent (the source uses `metadata` and `details`). `model` is absent.**
- **Consumer reads** (useSessionTimeline.ts:454-499): `detail.toolCallId`, `detail.state`, `detail.toolName`, `detail.title`, `detail.rawInput`, `detail.rawOutput`, `detail.executionId`, `detail.acpSessionId`.
- **Verdict**: **Field mismatch on `rawOutputMetadata` and `model`**: declared on Web type, never sent. The consumers I read don't access these by name, so latent.

### 8.2 Events the Web listens for that have **no emitter**

These are the "dead wire" cases — the Web's `LiveTaskProvider` switch arm or `onAgentEvent` subscription will never receive a payload in production. Listed for completeness.

`comment_added`, `agent_started`, `agent_completed`, `agent_paused`, `agent_error`, `agent_blocked`, `approval_requested`, `merge_queued`, `merge_started`, `merge_completed`, `merge_failed`, `agent_conflict_resolution_started`, `agent_conflict_resolution_completed`, `agent_conflict_resolution_failed`, `check_started`, `check_update`, `check_suite_status_changed`, `stage_task_update`, `base_drift_detected`, `rebase_opportunity`, `user_attention_requested`, `rebase_started`, `rebase_progress`, `rebase_completed`, `rebase_conflict`, `coder_recovery_status`, `ralph_task_update`, `ralph_loop_progress`, `plan_round_start`, `plan_session_update`, `plan_round_complete`, `integration_started`, `integration_completed`, `integration_failed`, `integration_step_updated`, `agent_usage_update`, `tool_call`, `agent_text_chunk`, `main_tool_call`, `agent_liveness_status`, `integration_preflight_refreshed`, `schedule_triggered`, `schedule_completed`, `schedule_failed`.

For each, the Web has declared a payload shape in `EventMap` / `AgentDetailEventMap` (where applicable). The contract is "real" in the sense of a TypeScript type, but the runtime contract is **absent** — no source emits under the bus name.

### 8.3 Class-name events (the 17 silently-dropped names)

`WorkflowRunStarted`, `WorkflowRunResumed`, `WorkflowRunPaused`, `WorkflowRunStopped`, `WorkflowRunFailed`, `WorkflowRunCompleted`, `StageStarted`, `StageCompleted`, `StageFailed`, `StageApprovalRequested`, `StageApprovalResolved`, `TaskCompleted`, `TaskFailed`, `CheckPassed`, `CheckFailed`, `CheckPending`, `RepairScheduled`.

The Web has **no `EventMap` entry** for any of these. The closest the Web comes is `stage_changed` (already a live event) and `check_update` (a registered-but-not-emitted name). If these become live (per §6), the payload shape from the source is the entire `WorkflowDomainEventDto` (`Infrastructure/Events/IEventStore.cs:11-17`):

```ts
{ id, source, type, data, time, specVersion }
```

where `data` is the discriminated `WorkflowEvent` (e.g. `StageStarted(stage)`). The Web would have to define a new payload shape for each of these names and add `case` arms in `LiveTaskProvider` (or in the agent EventTarget if they qualify as "agent detail").

---

## 9. Test coverage of the bus path

### 9.1 Tests that exercise the bus end-to-end

| Test | File:line | What it verifies |
|---|---|---|
| `EventBusSpecs.Emit_WithSubscriber_ReceivesEvent` | `tests/.../SystemSpecs/EventBusSpecs.cs:20-29` | `bus.Emit("test", payload)` delivers to `bus.On("test", handler)` |
| `EventBusSpecs.Emit_NoSubscriber_DoesNotThrow` | `tests/.../SystemSpecs/EventBusSpecs.cs:34-37` | `bus.Emit` on an unknown name is a no-op (this is the *current* behavior, and it is exactly why the class-name events are silently dropped) |
| `EventBusSpecs.Off_RemovesSubscriber` | `tests/.../SystemSpecs/EventBusSpecs.cs:42-52` | `bus.Off` unsubscribes |
| `EventBusSpecs.Emit_MultipleSubscribers_AllReceive` | `tests/.../SystemSpecs/EventBusSpecs.cs:57-68` | fan-out works |
| `EventBusSpecs.Emit_DifferentEventTypes_Isolated` | `tests/.../SystemSpecs/EventBusSpecs.cs:73-84` | namespacing |
| `EventBusSpecs.Emit_SlowSubscriber_DoesBlockCaller` | `tests/.../SystemSpecs/EventBusSpecs.cs:89-104` | synchronous dispatch |
| `EventBridgeSpecs.EventBusEvent_ForProjectScopedPayload_IsSentToProjectGroup` | `tests/.../SystemSpecs/EventBridgeSpecs.cs:15-37` | bridge routes an `IProjectScoped` payload to `project:<id>` group |
| `EventBridgeSpecs.EventBusEvent_WithoutProjectScope_IsSentToGlobalGroup` | `tests/.../SystemSpecs/EventBridgeSpecs.cs:42-56` | bridge routes unscoped payloads to `project:global` |
| `WorkflowEventSpecs.EventBus_DirectEmit_Works` | `tests/.../Workflow/Grain/WorkflowEventSpecs.cs:30-36` | direct bus emit through the test fixture works |
| `WorkflowEventSpecs.WorkflowStart_EmitsStageChanged` | `tests/.../Workflow/Grain/WorkflowEventSpecs.cs:41-63` | **end-to-end**: `wf.StartAsync()` → bus subscriber receives `stage_changed` with `action === "started"` and `stage === "plan"` |
| `WorkflowEventSpecs.WorkflowPause_EmitsStageChanged` | `tests/.../Workflow/Grain/WorkflowEventSpecs.cs:68-93` | **end-to-end**: `wf.PauseAsync("user-requested")` → bus subscriber receives `stage_changed` with `action === "paused"` and `reason === "user-requested"` |
| `WorkflowEventSpecs.WorkflowResume_EmitsStageChanged` | `tests/.../Workflow/Grain/WorkflowEventSpecs.cs:98-123` | **end-to-end**: `wf.ResumeAsync()` → bus subscriber receives `stage_changed` with `action === "resumed"` |

These are the **only** tests in the entire server suite that assert a bus delivery.

### 9.2 Tests that test the DB write but never the bus path

| Test | File:line | What it verifies (DB only) |
|---|---|---|
| `WorkflowRunStoreSpecs.SaveAsync_IncrementsETagEvenAfterExternalMutation` | `tests/.../Workflow/Grain/WorkflowRunStoreSpecs.cs:20-74` | ETag bumps correctly. The store takes an `InMemoryEventBus` (line 38) but no subscriber is registered, so the bus emit happens silently. |
| `WorkflowRunStoreSpecs.SaveAsync_WithEvents_CommitsWorkflowRunAndEventsTogether` | `tests/.../Workflow/Grain/WorkflowRunStoreSpecs.cs:79-126` | DB rows are written with the right `Type` and `Data`. **No bus assertion.** This is the test that *would* catch the bus split — but it doesn't subscribe. |
| `EventStoreSpecs.AppendWorkflowEventAsync_StoresMinimalDomainEventRow` | `tests/.../SystemSpecs/EventStoreSpecs.cs:27-71` | DB row written. **No bus assertion.** |
| `EventStoreSpecs.ListWorkflowEventsAsync_ProjectsDomainEventsFromPayload` | `tests/.../SystemSpecs/EventStoreSpecs.cs:76-93` | read model. **No bus assertion.** |
| `EventStoreSpecs.AgentSessionStore_StoresSessionStateAndDomainEventsInOneCommit` | `tests/.../SystemSpecs/EventStoreSpecs.cs:98-141` | DB rows. **No bus assertion.** |
| `IssueWorkflowProductLoopSpecs.IssueStart_RunnerCompletesWorkflow_IssueBecomesDone` | `tests/.../Issue/Api/IssueWorkflowProductLoopSpecs.cs:44-97` | HTTP integration. Asserts that the *events API* returns `WorkflowRunStarted`/`TaskCompleted`/`CheckPassed`/`WorkflowRunCompleted` events. **Never subscribes to the bus**, never checks SignalR. The events API reads from the DB row, not the bus. |
| `WorkflowEventApiSpecs.*` | `tests/.../Workflow/Api/WorkflowEventApiSpecs.cs:48, 50, 53, 89` | `Assert.Contains(events, e => e.Type == nameof(WorkflowRunStarted))` etc. — DB row read. |
| `WorkflowEventSerializationSpecs.*` | `tests/.../Workflow/Api/WorkflowEventSerializationSpecs.cs:17, 31` | payload shape only. **No bus assertion.** |

**The bus path for the 17 class-name events is not covered by any test.** A test that subscribes to `"StageStarted"` (or any class name) and runs a workflow start-to-completion through `WorkflowGrain` would catch the silent-drop bug immediately. None exists.

### 9.3 What the tests would need to catch the class-name drop

A test like:

```csharp
[Fact]
public async Task WorkflowStart_EmitsStageStarted()
{
    var received = new List<string>();
    _fixture.EventBus.On("StageStarted", _ => received.Add("StageStarted"));
    _fixture.EventBus.On("stage_changed", _ => received.Add("stage_changed"));
    var wf = _fixture.Grains.GetGrain<IWorkflowGrain>(workflowId);
    await SeedWorkflowTemplateAsync(workflowId, MohistWorkflow.Definition);
    await wf.StartAsync(TestInput());
    Assert.Contains("stage_changed", received);   // passes today
    Assert.Contains("StageStarted", received);     // would fail: no bus handler registered
}
```

Adding this test (one per class name of interest: `StageStarted`, `TaskCompleted`, `StageApprovalRequested`, `WorkflowRunFailed`, `CheckPassed`) would have flagged the class-name drop in CI.

---

## 10. Summary table: bus-name → emit-count → subscriber-count → web-listener-count

Counts derived from §1, §2, §4. "Bus emit" = a production `_eventBus.Emit(busName, ...)` call site. "Bridge sub" = 1 if the bus name is in `EventBusEventTypes.All`. "Web listener" = 1 if a `case` arm in `LiveTaskProvider.handleEvent` or a `dispatchAgentEvent`/per-widget `onAgentEvent` subscriber reads the name. (The `OnEvent` signal itself is always 1 for every name, but here I count logical listeners.)

The class-name events (17 names) appear at the bottom for completeness, but they are **not in `EventBusEventTypes.All`**, so they are not on the bus-registered side.

| Bus name | Production emit sites | Bus-registered? | Bridge subscribes? | Web listener? | Status |
|---|---|---|---|---|---|
| `stage_changed` | 1 (WorkflowGrain:892) | yes | 1 | 1 | **Live (end-to-end works)** |
| `coder_session_started` | 1 (AgentSessionGrain:284) | yes | 1 | 1 | **Live** |
| `coder_session_completed` | 1 (AgentSessionGrain:354) | yes | 1 | 1 | **Live** |
| `coder_session_failed` | 1 (AgentSessionGrain:354) | yes | 1 | 1 | **Live** |
| `coder_session_cancelled` | 1 (AgentSessionGrain:354) | yes | 1 | 1 | **Live** |
| `coder_session_status_changed` | 1 (AgentSessionGrain:345) | yes | 1 | 1 | **Live** |
| `coder_text_chunk` | 1 (AgentSessionGrain:301) | yes | 1 | 1 | **Live** |
| `coder_thought_chunk` | 1 (AgentSessionGrain:311) | yes | 1 | 1 | **Live** |
| `coder_tool_call` | 1 (AgentSessionGrain:324) | yes | 1 | 1 | **Live** |
| `comment_added` | 0 | yes | 1 | 1 | **Dead registration** (IssueGrain.AddCommentAsync writes DB but never emits) |
| `agent_started` | 0 | yes | 1 | 1 | Dead |
| `agent_completed` | 0 | yes | 1 | 1 | Dead |
| `agent_paused` | 0 | yes | 1 | 1 | Dead |
| `agent_error` | 0 | yes | 1 | 1 | Dead |
| `agent_blocked` | 0 | yes | 1 | 1 | Dead |
| `approval_requested` | 0 | yes | 1 | 1 | Dead (and the WorkflowGrain `On` switch for `StageApprovalRequested` only disables heartbeat, never emits) |
| `tool_call` | 0 | yes | 1 | 0 (no `onAgentEvent('tool_call', ...)`; only the runtime-event type `e.Type === 'tool_call'` in DB-layer queries) | Dead for live |
| `agent_text_chunk` | 0 | yes | 1 | 0 (declared in `AgentDetailEventMap` but no consumer; the bridge subscribes but no source emits) | Dead |
| `main_tool_call` | 0 | yes | 1 | 0 (declared in `AgentDetailEventMap`, never consumed) | Dead |
| `coder_recovery_status` | 0 | yes | 1 | 1 (consumed in `useSessionTimeline.ts:543` and `useSessionTranscript.ts:979`) | Dead — listeners exist but no emitter |
| `plan_session_update` | 0 | yes | 1 | 1 (consumed in `useSessionTimeline.ts:375`) | Dead |
| `plan_round_start` | 0 | yes | 1 | 1 (consumed in `useSessionTimeline.ts:325`) | Dead |
| `plan_round_complete` | 0 (not in `EventBusEventTypes.All`) | n/a | 0 | 1 (consumed in `useSessionTimeline.ts:389`, in `AGENT_DETAIL_EVENTS`) | **Triple-dead**: not registered, no emitter, web listens |
| `merge_queued` | 0 | yes | 1 | 1 | Dead |
| `merge_started` | 0 | yes | 1 | 1 | Dead |
| `merge_completed` | 0 | yes | 1 | 1 | Dead (no source emits despite the success-toast consumer in LiveTaskProvider:168-170) |
| `merge_failed` | 0 | yes | 1 | 1 | Dead |
| `merge_blocked` | 0 | yes | 1 | 0 | Dead (no Web listener either) |
| `agent_conflict_resolution_started` | 0 | yes | 1 | 1 | Dead |
| `agent_conflict_resolution_completed` | 0 | yes | 1 | 1 | Dead |
| `agent_conflict_resolution_failed` | 0 | yes | 1 | 1 | Dead |
| `rebase_started` | 0 | yes | 1 | 1 (LiveTaskProvider + dead `onRebaseEvent` in WorktreePanel) | Dead |
| `rebase_progress` | 0 | yes | 1 | 1 (only `onRebaseEvent`; the LiveTaskProvider doesn't switch on it) | Dead — and the LiveTaskProvider never invalidates on rebase_progress (so even if a source emitted, the only consumer would be `WorktreePanel` via the dead dispatcher) |
| `rebase_completed` | 0 | yes | 1 | 1 (LiveTaskProvider + dead `onRebaseEvent`) | Dead |
| `rebase_conflict` | 0 | yes | 1 | 1 (LiveTaskProvider + dead `onRebaseEvent`) | Dead |
| `schedule_triggered` | 0 | yes | 1 | 0 | Dead |
| `schedule_completed` | 0 | yes | 1 | 0 | Dead |
| `schedule_failed` | 0 | yes | 1 | 0 | Dead |
| `stage_task_update` | 0 | yes | 1 | 1 (LiveTaskProvider:231-235 + WorkflowView:1161) | Dead (in `AGENT_DETAIL_EVENTS`, no emitter) |
| `integration_started` | 0 | yes | 1 | 0 | Dead |
| `integration_completed` | 0 | yes | 1 | 0 | Dead |
| `integration_failed` | 0 | yes | 1 | 0 | Dead |
| `integration_preflight_refreshed` | 0 | yes | 1 | 0 | Dead |
| `integration_step_updated` | 0 | yes | 1 | 0 | Dead |
| `ralph_task_update` | 0 | yes | 1 | 1 (LiveTaskProvider:73-90 + useSessionTimeline:504) | Dead (in `AGENT_DETAIL_EVENTS`, no emitter; but the Web has a *rich* consumer surface that will never fire) |
| `ralph_loop_progress` | 0 | yes | 1 | 1 (LiveTaskProvider:96 + useSessionTimeline:531) | Dead |
| `agent_usage_update` | 0 | yes | 1 | 1 (LiveTaskProvider:103 + useCoderSessions:131) | Dead — runtime-event loop uses this as a `Type` filter, never as a bus name |
| `agent_liveness_status` | 0 (not in `EventBusEventTypes.All`) | n/a | 0 | 1 (LiveTaskProvider:102 + useSessionTimeline:585 + useSessionTranscript:1014) | **Triple-dead**: not registered, no emitter, web listens |
| `check_started` | 0 | yes | 1 | 1 | Dead |
| `check_update` | 0 | yes | 1 | 1 | Dead (but the fix is easy: emit from `WorkflowGrain.On` when handling `CheckPassed`/`CheckFailed`/`CheckPending`) |
| `check_suite_status_changed` | 0 | yes | 1 | 1 | Dead |
| `base_drift_detected` | 0 | yes | 1 | 1 | Dead |
| `rebase_opportunity` | 0 | yes | 1 | 1 | Dead |
| `user_attention_requested` | 0 | yes | 1 | 1 | Dead |

**Class-name events (NOT in `EventBusEventTypes.All`; emitted by `WorkflowRunStore.Publish` and `EventStore.AppendWorkflowEventAsync`):**

| Event name | Production emit sites | Bus-registered? | Bridge subscribes? | Web listener? | Status |
|---|---|---|---|---|---|
| `WorkflowRunStarted` | 1 (WorkflowRunStore:107) | **no** | **0** | 0 | **Silently dropped at bus** |
| `WorkflowRunResumed` | 1 (same) | no | 0 | 0 | Silently dropped |
| `WorkflowRunPaused` | 1 (same) | no | 0 | 0 | Silently dropped |
| `WorkflowRunStopped` | 1 (same) | no | 0 | 0 | Silently dropped |
| `WorkflowRunFailed` | 1 (same) | no | 0 | 0 | Silently dropped |
| `WorkflowRunCompleted` | 1 (same) | no | 0 | 0 | Silently dropped |
| `StageStarted` | 1 (same) | no | 0 | 0 | Silently dropped |
| `StageCompleted` | 1 (same) | no | 0 | 0 | Silently dropped |
| `StageFailed` | 1 (same) | no | 0 | 0 | Silently dropped |
| `StageApprovalRequested` | 1 (same) | no | 0 | 0 | Silently dropped |
| `StageApprovalResolved` | 1 (same) | no | 0 | 0 | Silently dropped |
| `TaskCompleted` | 1 (same) | no | 0 | 0 | Silently dropped |
| `TaskFailed` | 1 (same) | no | 0 | 0 | Silently dropped |
| `CheckPassed` | 1 (same) | no | 0 | 0 | Silently dropped |
| `CheckFailed` | 1 (same) | no | 0 | 0 | Silently dropped |
| `CheckPending` | 1 (same) | no | 0 | 0 | Silently dropped |
| `RepairScheduled` | 1 (same) | no | 0 | 0 | Silently dropped |

**Tally**:
- 9 / 45 registered bus names are live end-to-end (8 actually emit, 1 (`stage_changed`) emits from `WorkflowGrain` directly).
- 36 / 45 are dead-registered (declared in `EventBusEventTypes.All` but no source emits).
- 17 additional class-name events are emitted but not registered, so they are silently dropped at the bus.
- 1 bus name (`plan_round_complete`) and 1 bus name (`agent_liveness_status`) are listed in `AGENT_DETAIL_EVENTS` (the Web-side list) but **not** in `EventBusEventTypes.All` — triple-dead: no bridge subscription, no emitter, but the Web has a consumer for them.
- The only test that exercises the bus path for any of these 17 class names is the direct "what happens when we call `bus.Emit`" test in `EventBusSpecs`. No test ever asserts that a `WorkflowRunStarted` (or any other class name) reaches a subscriber from a real workflow run.
