---
purpose: "Redesign the cross-grain event response mechanism. Domain events are the origin; downstream reactions (Issue, AgentSession, Runner, Web UI) consume them via the bus."
include:
  - "Event taxonomy (bus names + payload schemas)."
  - "Subscription model and project scoping."
  - "The Issue ← Workflow canonical example end-to-end."
  - "Migration path from the current direct-call + 36 dead registrations to a fully event-driven design."
  - "Bus durability / replay plan."
exclude:
  - "WorkflowRun internal transitions; see workflow-domain-events.md."
  - "Workflow scheduling internals; see workflow-scheduling.md."
  - "Workflow template variables; see workflow-template-variables.md."
  - "HTTP API contract; see design/architecture.md."
---

# Event Response Mechanism

本文重新设计 mohist 的事件响应机制。目标：让**领域事件成为事件起源**（不是手工 emit），让**前端 SignalR 监听、后续业务触发、业务流程编排**都尽量走事件总线，让 Issue 生命周期根据 Workflow 事件自动推进。

## Problem

当前实现是"声明但未接线"的状态：

1. **36 / 45 个 bus 注册名是死的**（`EventBusEventTypes.All` 里 45 个字符串，只有 9 个有真正的 emit 调用站点）。`approval_requested`、`merge_completed`、`rebase_conflict`、`stage_task_update` 等 36 个名被 Web 监听、但后端从来不发。
2. **18 个 workflow 领域事件被静默丢弃**。`WorkflowRunStore.Publish` 用 C# 类名（`StageStarted`、`TaskCompleted`、`CheckPassed` 等）emit 到总线，但 `EventBusEventTypes.All` 里没有这些名字，bus 找到 0 个 subscriber，事件落空。整条 workflow 状态机变更流（除了手工 `EmitStageChanged("stage_changed")` 之外）对 Web 完全不可见。
3. **业务编排全部用直接调用**。`WorkflowGrain.On(WorkflowRunCompleted, ...)` 调 `DispatchCompletedHooksAsync` → `IWorkflowCompletionHook.OnCompletedAsync` → `IssueWorkflowCompletionHook.OnCompletedAsync` → `IssueGrain.CompleteWorkAsync` → `_issue.Complete(...)`。这是唯一一个跨 grain 编排点，且只为 `Completed` 一个 case 工作。
4. **3 个 P0 状态机死锁**。
   - `WorkflowRunFailed` → Issue 永远停在 `InProgress`（`IssueWorkflowCompletionHook` 只听 `Completed`）。
   - `WorkflowRunStopped` → Issue 同样不动。
   - `_activeWorkflowRunId` 永远不清，reopen 之后 `StartWorkAsync` 抛 `Issue #N already has workflow wr_xxx`。
5. **前端 4 / 20 个用户可见状态是 push-driven**。其余 16 个靠 poll、user-action refresh、或干脆死了。最大的缺口是 home kanban — 任何 backend 活动都不会让看板变化。
6. **Bus 不可持久**。`InMemoryEventBus` 是进程内的 `ConcurrentDictionary`；silo 重启期间或 IssueGrain 失活期间发出的事件被丢。`IWorkflowCompletionHook` 失败被 swallow（`WorkflowGrain.cs:1007-1010`），workflow 持久化但 Issue 永远不动。

## Design Invariant

```text
Domain decides.
Commit logs the fact.
Bus broadcasts the fact.
Subscribers react.
Reactions never decide new facts directly; they request a new command, which is re-decided by the domain.
```

四个边界：

- **WorkflowRun / Issue / AgentSession domain method** 是"事实来源"。校验命令、修改状态、返回刚发生的事实。
- **Owning grain** (WorkflowGrain / IssueGrain / AgentSessionGrain) 负责：持久化、把持久化后的事实 emit 到 bus、对同 grain 内的 reactions 执行。
- **IEventBus** 是 in-process substrate，把事实 fan-out 给所有 subscriber。
- **Subscriber**（Web via SignalR / IssueGrain / AgentSessionGrain / Hook services）消费事实并执行 reactions。

Reaction 不能直接 mutate 另一个 aggregate 的状态。Subscriber 必须发出**新的命令**（`IIssueGrain.AbortWorkAsync` / `IAgentSessionGrain.MarkFailedAsync`），由 owning grain 的 domain method 重新校验、修改、emit。

这与 `workflow-domain-events.md` 的"WorkflowRun 决定 / WorkflowGrain 反应"边界一致，但把"反应"从 in-grain switch 扩展到 in-process bus + 持久化 subscriber。

## Event Taxonomy

`type` 字段是 CloudEvents 1.0.2 §3.1 规定的 required attribute，producer 自由选择值。我们用 reverse-DNS（`com.mohist.<domain>.<aggregate>.<verb>`），已存在的 snake_case 名（`coder_session_failed` 等）保留——它们都是合法 CloudEvents `type` 值。

### Workflow lifecycle

| CloudEvents `type` | Source | Extensions | Triggers |
|--------------------|--------|------------|----------|
| `com.mohist.workflow.run.started` | `/mohist/workflow/{wrId}` | `projectid`, `workflowrunid`, `issueno` | IssueGrain → `Status = InProgress` (idempotent) |
| `com.mohist.workflow.run.resumed` | `/mohist/workflow/{wrId}` | `projectid`, `workflowrunid`, `issueno` | IssueGrain → reset heartbeat |
| `com.mohist.workflow.run.paused` | `/mohist/workflow/{wrId}` | `projectid`, `workflowrunid`, `issueno` | IssueGrain → noop (preserves InProgress) |
| `com.mohist.workflow.run.stopped` | `/mohist/workflow/{wrId}` | `projectid`, `workflowrunid`, `issueno` | IssueGrain → `AbortWorkAsync` → `Status = Cancelled`, clear `_activeWorkflowRunId` |
| `com.mohist.workflow.run.completed` | `/mohist/workflow/{wrId}` | `projectid`, `workflowrunid`, `issueno` | IssueGrain → `CompleteWorkAsync` → `Status = Done`, clear `_activeWorkflowRunId` |
| `com.mohist.workflow.run.failed` | `/mohist/workflow/{wrId}` | `projectid`, `workflowrunid`, `issueno` | IssueGrain → `AbortWorkAsync` → `Status = Cancelled` (or new `Failed`), clear `_activeWorkflowRunId` |
| `com.mohist.workflow.run.retrying` | `/mohist/workflow/{wrId}` | `projectid`, `workflowrunid`, `issueno`, `attempt` | Web → toast "retrying" |
| `com.mohist.workflow.run.rerunning` | `/mohist/workflow/{wrId}` | `projectid`, `workflowrunid`, `issueno`, `attempt` | Web → toast "rerunning" |

### Stage lifecycle

| CloudEvents `type` | Source | Extensions | Triggers |
|--------------------|--------|------------|----------|
| `com.mohist.workflow.stage.started` | `/mohist/workflow/{wrId}/stage/{stage}` | `projectid`, `workflowrunid`, `issueno`, `stage` | Web → stage bar updates; AgentSessionGrain → fresh stage context |
| `com.mohist.workflow.stage.completed` | `/mohist/workflow/{wrId}/stage/{stage}` | `projectid`, `workflowrunid`, `issueno`, `stage` | Web → stage bar marks complete |
| `com.mohist.workflow.stage.failed` | `/mohist/workflow/{wrId}/stage/{stage}` | `projectid`, `workflowrunid`, `issueno`, `stage` | Web → stage bar marks failed |
| `com.mohist.workflow.stage.approval-requested` | `/mohist/workflow/{wrId}/stage/{stage}` | `projectid`, `workflowrunid`, `issueno`, `stage`, `requestedAt` | Web → **the "needs approval" badge appears**; **closes the `approval_requested` dead-registration gap** |
| `com.mohist.workflow.stage.approval-resolved` | `/mohist/workflow/{wrId}/stage/{stage}` | `projectid`, `workflowrunid`, `issueno`, `stage`, `result` | Web → toast; workflow advances |
| `com.mohist.workflow.stage.lock-acquired` | `/mohist/workflow/{wrId}/stage/{stage}` | `projectid`, `workflowrunid`, `issueno`, `stage` | Web → live lock indicator |
| `com.mohist.workflow.stage.lock-released` | `/mohist/workflow/{wrId}/stage/{stage}` | `projectid`, `workflowrunid`, `issueno`, `stage` | Web → lock indicator clears |

### Task lifecycle

| CloudEvents `type` | Source | Extensions | Triggers |
|--------------------|--------|------------|----------|
| `com.mohist.workflow.task.started` | `/mohist/workflow/{wrId}/task/{taskId}` | `projectid`, `workflowrunid`, `issueno`, `stage`, `taskid`, `attempt` | AgentSessionGrain → ensure session row; Web → live task indicator |
| `com.mohist.workflow.task.completed` | `/mohist/workflow/{wrId}/task/{taskId}` | `projectid`, `workflowrunid`, `issueno`, `stage`, `taskid`, `durationms` | AgentSessionGrain → close session; Web → task list marks complete |
| `com.mohist.workflow.task.failed` | `/mohist/workflow/{wrId}/task/{taskId}` | `projectid`, `workflowrunid`, `issueno`, `stage`, `taskid`, `retryable` | AgentSessionGrain → mark failed; WorkflowGrain → retry or fail stage |
| `com.mohist.workflow.task.progress` | `/mohist/workflow/{wrId}/task/{taskId}` | `projectid`, `workflowrunid`, `taskid`, `percent` | Web → progress bar |

### Check lifecycle

| CloudEvents `type` | Source | Extensions | Triggers |
|--------------------|--------|------------|----------|
| `com.mohist.workflow.check.started` | `/mohist/workflow/{wrId}/check/{checkName}` | `projectid`, `workflowrunid`, `issueno`, `stage`, `checkname` | Web → check list active |
| `com.mohist.workflow.check.passed` | `/mohist/workflow/{wrId}/check/{checkName}` | `projectid`, `workflowrunid`, `issueno`, `stage`, `checkname`, `durationms` | Web → green check |
| `com.mohist.workflow.check.failed` | `/mohist/workflow/{wrId}/check/{checkName}` | `projectid`, `workflowrunid`, `issueno`, `stage`, `checkname`, `autofixed`, `repairattempt` | Web → red check + toast; repair scheduling |
| `com.mohist.workflow.check.pending` | `/mohist/workflow/{wrId}/check/{checkName}` | `projectid`, `workflowrunid`, `issueno`, `stage`, `checkname` | Web → grey check |
| `com.mohist.workflow.repair-scheduled` | `/mohist/workflow/{wrId}/check/{checkName}` | `projectid`, `workflowrunid`, `issueno`, `stage`, `checkname`, `taskids`, `attempt` | Web → "repairing" badge |

### Runner / session lifecycle

| CloudEvents `type` | Source | Extensions | Triggers |
|--------------------|--------|------------|----------|
| `com.mohist.runner.registered` | `/mohist/runner/{runnerId}` | `runnerid`, `hostname` | Web → runner status panel updates |
| `com.mohist.runner.unregistered` | `/mohist/runner/{runnerId}` | `runnerid` | Web → runner panel updates |
| `com.mohist.runner.disconnected` | `/mohist/runner/{runnerId}` | `runnerid`, `reason` | AgentSessionGrain → `FailIfRunningAsync`; WorkflowGrain → mark task failed; **closes the audit gap** |
| `com.mohist.runner.dispatched` | `/mohist/workflow/{wrId}/work/{workId}` | `projectid`, `workflowrunid`, `issueno`, `workid`, `worktype`, `stage`, `runnerid` | AgentSessionGrain → pre-allocate row; observability |
| `com.mohist.runner.reported-result` | `/mohist/workflow/{wrId}/work/{workId}` | `projectid`, `workflowrunid`, `workid`, `worktype`, `status`, `runnerid` | Web → "runner reported" badge; telemetry |
| `com.mohist.workflow.lease-expired` | `/mohist/workflow/{wrId}/work/{workId}` | `projectid`, `workflowrunid`, `workid`, `runnerid`, `ageseconds` | AgentSessionGrain → mark failed; **detects stuck leases independent of TCP** |
| `com.mohist.agent-session.started` | `/mohist/agent-session/{sessionId}` | `projectid`, `workflowrunid`, `sessionid`, `acpsessionid`, `model` | Web → session card appears |
| `com.mohist.agent-session.completed` | `/mohist/agent-session/{sessionId}` | `projectid`, `workflowrunid`, `sessionid`, `durationms` | Web → green session card |
| `com.mohist.agent-session.failed` | `/mohist/agent-session/{sessionId}` | `projectid`, `workflowrunid`, `sessionid`, `reason` | Web → red session card; WorkflowGrain (via subscription) → mark task failed |
| `com.mohist.agent-session.cancelled` | `/mohist/agent-session/{sessionId}` | `projectid`, `workflowrunid`, `sessionid`, `reason` | Web → grey session card |
| `com.mohist.agent-session.status-changed` | `/mohist/agent-session/{sessionId}` | `projectid`, `workflowrunid`, `sessionid`, `status`, `lastdataat` | Web → status badge updates |
| `com.mohist.agent-session.runtime-event` | `/mohist/agent-session/{sessionId}` | `projectid`, `workflowrunid`, `sessionid`, `runtimeeventtype` | Web → live prompt / usage / model / tool updates |

Legacy names kept for back-compat: `coder_session_started`, `coder_text_chunk`, `coder_thought_chunk`, `coder_tool_call`, `coder_session_status_changed`, `coder_session_completed`, `coder_session_failed`, `coder_session_cancelled`, `coder_recovery_status`, `ralph_task_update`, `ralph_loop_progress`, `plan_round_start`, `plan_session_update`, `plan_round_complete`, `agent_liveness_status`, `agent_usage_update`, `agent_text_chunk`, `main_tool_call`, `stage_changed`, `comment_added`, `agent_started`, `agent_completed`, `agent_paused`, `agent_error`, `agent_blocked`, `merge_queued`, `merge_started`, `merge_completed`, `merge_failed`, `merge_blocked`, `rebase_started`, `rebase_progress`, `rebase_completed`, `rebase_conflict`, `agent_conflict_resolution_started`, `agent_conflict_resolution_completed`, `agent_conflict_resolution_failed`, `check_started`, `check_update`, `check_suite_status_changed`, `stage_task_update`, `integration_started`, `integration_step_updated`, `integration_completed`, `integration_failed`, `integration_preflight_refreshed`, `base_drift_detected`, `rebase_opportunity`, `user_attention_requested`, `tool_call`, `schedule_triggered`, `schedule_completed`, `schedule_failed`.

### Issue lifecycle (new — driven by the redesign)

| CloudEvents `type` | Source | Extensions | Triggers |
|--------------------|--------|------------|----------|
| `com.mohist.issue.created` | `/mohist/project/{projectId}/issue/{number}` | `projectid`, `issueno` | Web → kanban adds card |
| `com.mohist.issue.workflow-started` | `/mohist/project/{projectId}/issue/{number}` | `projectid`, `issueno`, `workflowrunid` | Web → card transitions to InProgress column |
| `com.mohist.issue.completed` | `/mohist/project/{projectId}/issue/{number}` | `projectid`, `issueno`, `workflowrunid`, `completedat` | Web → card transitions to Done column; the canonical "issue 根据 workflow.run.completed 把自己 done 掉" event |
| `com.mohist.issue.cancelled` | `/mohist/project/{projectId}/issue/{number}` | `projectid`, `issueno`, `reason`, `cancelledat` | Web → card transitions to Cancelled column |
| `com.mohist.issue.reopened` | `/mohist/project/{projectId}/issue/{number}` | `projectid`, `issueno`, `reason` | Web → card returns to Backlog |
| `com.mohist.issue.attention-required` | `/mohist/project/{projectId}/issue/{number}` | `projectid`, `issueno`, `reason`, `nextaction` | Web → red badge |

### Project / epic (reserved for future)

`com.mohist.project.created`, `com.mohist.epic.created`, `com.mohist.epic.issue-linked`, etc.

## The Issue ← Workflow Example End-to-End

用户期望的具体行为：

> 审批后保持 Review → MergeQueue → 回调设 Done

实际上 mohist 当前把 approve + integrate 合并在同一个 workflow run 中跑（`check` 阶段 approval → `integrate:merge` task → `WorkflowRunCompleted`），所以"Review → MergeQueue → 回调"在当前 4-stage yaml 下不是一个独立 sub-state，而是一个事件流。新设计处理如下：

### Sequence on user approve — CloudEvents envelopes shown

```text
1. POST /api/projects/{ref}/issues/{n}/approve
   → IIssueGrain.ApproveAsync() (forwards to workflow)
   → IWorkflowGrain.ApproveAsync()
     → WorkflowRun.Approve() (domain)
       returns: [ StageApprovalResolved(approved) ]
     → WorkflowGrain.CommitAsync(events)
       → saves run state to DB
       → emits CloudEvents to bus (one per event):

       (a) CloudEvent {
             id: "f3a8c1e0-...",
             source: "/mohist/workflow/wr_abc123/stage/check",
             type: "com.mohist.workflow.stage.approval-resolved",
             specversion: "1.0",
             time: "2026-06-07T12:34:56.789Z",
             subject: "42",
             datacontenttype: "application/json",
             data: { "result": "approved", "stage": "check" },
             projectid: "mohist",
             workflowrunid: "wr_abc123",
             issueno: "42",
             stage: "check"
           }

       (b) CloudEvent { type: "com.mohist.workflow.stage.completed", subject: "42", data: { "stage": "check" }, ... }
       (c) CloudEvent { type: "com.mohist.workflow.stage.started", subject: "42", data: { "stage": "integrate", "requiresApproval": false }, ... }

       → On(e, reason) reactions (in-grain):
         * StageApprovalResolved → OnApprovalApprovedAsync → EmitStageChanged("approved")
         * StageCompleted → release stage lock
         * StageStarted → EnsureWorkHeartbeatAsync (start integrate:spec-sync)

2. Bus dispatches synchronously. Subscribers receive the CloudEvent envelope:
   * EventBridge → SignalR → Web:
       - LiveTaskProvider: type="stage_changed" (legacy, still works)
       - LiveTaskProvider: type="com.mohist.workflow.stage.completed" → invalidate ['issues', 42, 'mohist', 'workflow-timeline']
       - LiveTaskProvider: type="com.mohist.workflow.stage.started" → invalidate workflow-timeline
   * IssueGrain.On("com.mohist.workflow.stage.completed", e => noop)
   * (Future) AgentSessionGrain.On("com.mohist.workflow.stage.started", e => fresh stage context)

3. integrate stage runs:
   * integrate:spec-sync → integrate:archive-change → integrate:merge
   * Each task emits:
     - CloudEvent { type: "com.mohist.workflow.task.started", extensions: { taskid: "integrate:spec-sync" } }
     - CloudEvent { type: "com.mohist.workflow.task.completed", extensions: { taskid, durationms } }
   * WorkflowGrain dispatches each via RunnerGrain; RunnerGrain also emits:
     - CloudEvent { type: "com.mohist.runner.dispatched", extensions: { runnerid, worktype: "task" } }

4. Final integrate:merge completes:
   * WorkflowGrain.ProcessTaskResult → WorkflowRun.CompleteTask → returns [StageCompleted("integrate"), WorkflowRunCompleted]
   * CommitAsync emits:
     - CloudEvent { type: "com.mohist.workflow.task.completed", data: { taskId: "integrate:merge" } }
     - CloudEvent { type: "com.mohist.workflow.stage.completed", data: { stage: "integrate" } }
     - CloudEvent { type: "com.mohist.workflow.run.completed", data: { finalStage: "integrate" } }
   * Bus subscribers:
     * EventBridge → Web:
       - com.mohist.workflow.stage.completed → workflow-timeline invalidation
       - com.mohist.workflow.run.completed → issue list + workflow-timeline invalidation
     * **IssueGrain.On("com.mohist.workflow.run.completed", OnWorkflowCompleted)** (NEW subscription):
       - check: e.workflowrunid == _issue._activeWorkflowRunId && _issue._status == InProgress
       - calls _issue.Complete(wrId)
       - Issue.Transitions.Complete(): _status = Done, _activeWorkflowRunId = null
       - emits: CloudEvent { type: "com.mohist.issue.completed", subject: "42", data: { workflowRunId, completedAt } }
     * WorktreeCleanupHook.OnWorkflowCompleted → git.RemoveWorktreeAsync

5. Bus dispatches com.mohist.issue.completed:
   * EventBridge → Web → LiveTaskProvider:
     - invalidateQueries(['issues', 42, 'mohist']) → detail page shows "Done" status
     - invalidateQueries(['issues', 'list', 'mohist']) → kanban card moves to Done column
```

The Issue **reacting** to a workflow event is exactly the user's example: `com.mohist.workflow.run.completed` → `Issue.Status = Done`, no direct call. Both events are standard CloudEvents 1.0.2 envelopes; any CloudEvents-aware tooling (Knative Eventing, webhook receivers, third-party consumers) can subscribe to the bus output directly.

## Subscription Model

每个 grain 自己决定订阅哪些 `type`，订阅在 `OnActivateAsync` 注册、在 `OnDeactivateAsync` 反注册。订阅者收到的是 **CloudEvent envelope**（不是裸 payload），handler 从 envelope 读取 `id`/`source`/`type`/`subject` 和 extension attributes。

```csharp
public sealed class IssueGrain : Grain, IIssueGrain
{
    private IDisposable? _workflowCompletedSub;
    private IDisposable? _workflowStoppedSub;
    private IDisposable? _workflowFailedSub;
    private IDisposable? _stageApprovalRequestedSub;

    public override Task OnActivateAsync(CancellationToken ct)
    {
        var bus = _serviceProvider.GetRequiredService<IEventBus>();
        _workflowCompletedSub = bus.On(
            "com.mohist.workflow.run.completed", OnWorkflowCompleted);
        _workflowStoppedSub = bus.On(
            "com.mohist.workflow.run.stopped", OnWorkflowStopped);
        _workflowFailedSub = bus.On(
            "com.mohist.workflow.run.failed", OnWorkflowFailed);
        _stageApprovalRequestedSub = bus.On(
            "com.mohist.workflow.stage.approval-requested", OnStageApprovalRequested);
        return Task.CompletedTask;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        _workflowCompletedSub?.Dispose();
        _workflowStoppedSub?.Dispose();
        _workflowFailedSub?.Dispose();
        _stageApprovalRequestedSub?.Dispose();
        return Task.CompletedTask;
    }

    private void OnWorkflowCompleted(CloudEvent e)
    {
        // CloudEvent extension attributes carry the run id (no payload parse).
        var runId = e.ExtensionAttributes["workflowrunid"] as string;
        if (runId != _issue?.ActiveWorkflowRunId) return;
        if (_issue?.Status != IssueStatus.InProgress) return;

        // Issue a command, don't mutate directly.
        _ = CompleteWorkAsync(runId!);
    }

    private void OnWorkflowStopped(CloudEvent e) { /* mirror */ }
    private void OnWorkflowFailed(CloudEvent e)   { /* mirror */ }
}
```

### 关键规则

1. **Filter by extension attribute** (e.g. `workflowrunid`), not by parsing `data`. CloudEvent extension attributes are O(1) dictionary lookups; `data` is JSON that may require deserialization.
2. **Reactions call commands**, not mutators. `OnWorkflowCompleted` calls `CompleteWorkAsync(wrId)`, which is the same method the direct API would call. There is no second path to mutation.
3. **Idempotent guards in domain**. `Issue.Complete(wrId)` is a no-op if `wrId != _activeWorkflowRunId` or `Status != InProgress`. The handler doesn't have to be perfect; the domain enforces the invariant.
4. **Grain lifecycle = subscription lifecycle**. Re-activation in Orleans means re-subscribing. `IDisposable` disposal prevents double-firing on deactivation.
5. **The CloudEvent envelope is the contract**. Both in-process and Web consumers see the same shape. Extensions carry routing metadata; `data` carries the payload.

## Bus Implementation: Current vs Target

**The in-process bus is a synchronous [CloudEvents v1.0.2](https://github.com/cloudevents/spec/blob/v1.0.2/spec.md) transport.** It carries the standard CloudEvent envelope (data model) over a synchronous in-process fan-out (transport). We adopt the spec's required attributes, recommended extensions, and JSON format. We do not invent a parallel naming layer.

| Aspect | Current | Target (CloudEvents 1.0.2 in-process) |
|--------|---------|--------------------------------------|
| Data model | `Emit(string name, object payload)` (no envelope) | `Emit(CloudEvent envelope)` with all spec required attributes |
| `id` | missing | `Guid.NewGuid().ToString()` (required, unique per event) |
| `source` | missing | `Uri` like `/mohist/workflow/{wrId}` (required, identifies producer) |
| `type` | free-form string, hand-maintained in `EventBusEventTypes.All` | `string` per CloudEvents §3.1; reverse-DNS convention; **automatic registration** (no hand-maintained table) |
| `specversion` | missing | `"1.0"` (required) |
| `time` | missing (some emitters set it) | RFC 3339, set at emit |
| `subject` | missing | the issue number for issue-scoped events |
| `data` | ad-hoc `object` | JSON-serialized payload via `JsonEventFormatter` |
| `datacontenttype` | missing | `"application/json"` |
| Extensions (e.g. `projectid`, `workflowrunid`, `issueno`) | round-tripped through JSON in `EventBridge.ExtractProjectId` | first-class on the CloudEvent envelope, no JSON round-trip |
| Subscribe | `On(string name, Action<object> handler)` | `On(string type, Action<CloudEvent> handler)` or `On(Func<CloudEvent, bool> filter, ...)` |
| Dispatch | synchronous, in-emit-thread; per-handler try/catch | same; per-handler dead-letter row on unhandled exception |
| Project scoping | `EventBridge.ExtractProjectId` (JSON round-trip) | reads `cloudEvent["projectid"]` extension (O(1)) |
| Durability | none | outbox: every emit also writes to `Outbox` table; subscribers maintain watermarks; on restart, replay unprocessed |
| Library | custom `InMemoryEventBus` | **`CloudNative.CloudEvents`** (CNCF official .NET SDK) for the envelope; custom in-process transport layer; **`@cloudevents/sdk-typescript`** on Web |

### CloudEvent envelope on the wire

[CloudEvents 1.0.2 JSON format](https://github.com/cloudevents/spec/blob/v1.0.2/json-format.md) example for `com.mohist.workflow.run.completed`:

```json
{
  "id": "f3a8c1e0-9b2d-4e7a-8c5f-1d2e3f4a5b6c",
  "source": "/mohist/workflow/wr_abc123",
  "type": "com.mohist.workflow.run.completed",
  "specversion": "1.0",
  "time": "2026-06-07T12:34:56.789Z",
  "subject": "42",
  "datacontenttype": "application/json",
  "dataschema": "https://mohist.dev/schemas/workflow-run-completed.v1.json",
  "data": {
    "workflowRunId": "wr_abc123",
    "projectId": "mohist",
    "issueNumber": 42,
    "finalStage": "integrate"
  },
  "projectid": "mohist",
  "workflowrunid": "wr_abc123",
  "issueno": "42"
}
```

**Required attributes** (CloudEvents 1.0.2 §3.1, §3.2): `id`, `source`, `specversion`, `type`. We populate all four on every emit. `subject`, `time`, `datacontenttype`, `dataschema`, `data` are optional but we populate them too.

**Extension attributes**: anything not in the standard set. We use `projectid`, `workflowrunid`, `issueno`, `workid` as extensions so subscribers can filter on them without parsing `data`.

### Naming convention (`type`)

[CloudEvents §3.1](https://github.com/cloudevents/spec/blob/v1.0.2/spec.md#type): the producer chooses `type`. We adopt reverse-DNS as a convention. Existing names in `EventBusEventTypes.All` (`coder_session_failed`, `stage_changed`, etc.) are valid CloudEvents `type` values; new events follow the reverse-DNS pattern.

| Family | `type` value | Source | Notes |
|--------|--------------|--------|-------|
| Workflow run | `com.mohist.workflow.run.started` | WorkflowGrain | replaces class-name `WorkflowRunStarted` |
| Workflow run | `com.mohist.workflow.run.completed` | WorkflowGrain | drives Issue → Done |
| Workflow run | `com.mohist.workflow.run.failed` | WorkflowGrain | drives Issue → Cancelled (the missing back-edge) |
| Workflow run | `com.mohist.workflow.run.stopped` | WorkflowGrain | drives Issue → Cancelled |
| Workflow stage | `com.mohist.workflow.stage.started` | WorkflowGrain | replaces class-name `StageStarted` |
| Workflow stage | `com.mohist.workflow.stage.completed` | WorkflowGrain | replaces class-name `StageCompleted` |
| Workflow stage | `com.mohist.workflow.stage.approval-requested` | WorkflowGrain | **replaces the dead `approval_requested` registration**; the "needs approval" badge finally fires |
| Workflow task | `com.mohist.workflow.task.completed` | WorkflowGrain | replaces class-name `TaskCompleted` |
| Workflow check | `com.mohist.workflow.check.passed` | WorkflowGrain | replaces class-name `CheckPassed` |
| Agent session | `com.mohist.agent-session.started` | AgentSessionGrain | replaces `coder_session_started` |
| Agent session | `com.mohist.agent-session.completed` | AgentSessionGrain | replaces `coder_session_completed` |
| Agent session | `com.mohist.agent-session.failed` | AgentSessionGrain | replaces `coder_session_failed` |
| Runner | `com.mohist.runner.disconnected` | RunnerGrain + RunnerHub | closes the audit's "stuck session" gap |
| Issue | `com.mohist.issue.completed` | IssueGrain | new; the canonical example |
| Issue | `com.mohist.issue.cancelled` | IssueGrain | new |

**Legacy names stay** (no breaking change for the Web): `coder_session_started`, `coder_text_chunk`, `agent_paused`, etc. They are valid CloudEvents `type` values, even though they don't follow reverse-DNS. We add new events under reverse-DNS; old names are deprecated gradually.

### `EventBusEventTypes.All` becomes `EventCatalog`

The hand-maintained dispatch table is gone. `EventCatalog` is a **read-only inventory** of every `type` that should be emittable in the system, used for:

1. **Introspection / documentation** — generated into `docs/events.md` for the Web to consume.
2. **Test coverage assertion** — `EventCatalogTests` fails if an emit call uses a `type` not in the catalog, or a catalog entry has no producer.
3. **Schema reference** — each entry has `dataschema` (URI of the JSON schema for `data`).

```csharp
public sealed record EventCatalogEntry(
    string Type,           // "com.mohist.workflow.run.completed"
    Type PayloadType,      // typeof(WorkflowRunCompletedEvent)
    string? Description,
    Uri? DataSchema
);

public static class EventCatalog
{
    public static readonly IReadOnlyList<EventCatalogEntry> All = new[] {
        new("com.mohist.workflow.run.completed", typeof(WorkflowRunCompletedEvent), "...", null),
        new("com.mohist.workflow.run.failed", typeof(WorkflowRunFailedEvent), "...", null),
        // ...
    };
}
```

**The catalog is a documentation artifact, not a dispatch table.** The bus itself uses `CloudEvent.type` for routing. The catalog catches drift at test time, not at runtime.

### Producer helper

```csharp
public static class CloudEventFactory
{
    public static CloudEvent Create<TData>(
        string type,
        Uri source,
        TData data,
        string? subject = null,
        IReadOnlyDictionary<string, object>? extensions = null)
    {
        return new CloudEvent
        {
            Id = Guid.NewGuid().ToString(),
            Source = source,
            Type = type,
            SpecVersion = "1.0",
            Time = DateTimeOffset.UtcNow,
            Subject = subject,
            DataContentType = "application/json",
            Data = JsonSerializer.SerializeToElement(data, JsonOptions),
            ExtensionAttributes = Extensions(extensions),
        };
    }
}

// Usage in WorkflowGrain.CommitAsync:
_eventBus.Emit(CloudEventFactory.Create(
    type: "com.mohist.workflow.run.completed",
    source: new Uri($"/mohist/workflow/{workflowRunId}", UriKind.Relative),
    data: new { workflowRunId, projectId, finalStage = "integrate" },
    subject: issueNumber.ToString(),
    extensions: new Dictionary<string, object> {
        ["projectid"] = projectId,
        ["workflowrunid"] = workflowRunId,
        ["issueno"] = issueNumber.ToString(),
    }
));
```

### Subscriber (in-process)

```csharp
public override Task OnActivateAsync(CancellationToken ct)
{
    var bus = _serviceProvider.GetRequiredService<IEventBus>();
    _completedSub = bus.On(
        "com.mohist.workflow.run.completed",
        OnWorkflowCompleted);
    return Task.CompletedTask;
}

public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
{
    _completedSub?.Dispose();
    return Task.CompletedTask;
}

private void OnWorkflowCompleted(CloudEvent e)
{
    // Filter by extension attribute (O(1), no payload parse)
    if (e.ExtensionAttributes["workflowrunid"] as string != _issue?.ActiveWorkflowRunId) return;
    if (_issue?.Status != IssueStatus.InProgress) return;
    _ = CompleteWorkAsync((string)e.ExtensionAttributes["workflowrunid"]!);
}
```

### Subscriber (Web)

```typescript
import { CloudEvent, JSONParser } from '@cloudevents/sdk-typescript';

hub.on('OnEvent', (raw: string) => {
  const evt = JSONParser.parseEvent(raw) as CloudEvent;
  if (evt.type === 'com.mohist.workflow.run.completed') {
    const { projectid, issueno, workflowrunid } = evt.attributes;
    queryClient.invalidateQueries(['issues', issueno, projectid, 'workflow-timeline']);
  }
});
```

The Web receives a **standard CloudEvents 1.0.2 JSON envelope**. Any CloudEvents-aware client (Knative Eventing, CloudEvents SDKs in other languages, webhooks that expect CloudEvent format) can consume it directly.

### SignalR bridge

The bridge is a thin CloudEvents-to-SignalR shim. Each `Emit(CloudEvent)` becomes a SignalR call that forwards the JSON envelope as-is:

```csharp
public Task Emit(CloudEvent e)
{
    var json = JsonEventFormatter.EncodeStructuredModeMessage(e);  // spec 1.0.2 JSON
    var projectGroup = e.ExtensionAttributes.TryGetValue("projectid", out var p)
        ? $"project:{p}"
        : "project:global";
    return _hub.Clients.Group(projectGroup).OnEvent(json);
}
```

No more `ExtractProjectId` (JSON round-trip on every event). The project is on the envelope as an extension attribute.

## Library

- **Server**: `CloudNative.CloudEvents` (CNCF .NET SDK) — provides `CloudEvent`, `JsonEventFormatter`, validation per spec.
- **Web**: `@cloudevents/sdk-typescript` — provides `CloudEvent` parser, validates wire format.

Both libraries enforce the required attributes (`id`, `source`, `type`, `specversion`) and the JSON format. Misuse fails at construction time (server) or parse time (Web), not silently at runtime.

## Project Scoping

`MohistHub` joins each SignalR connection to `project:{projectId}` based on the `?projectId=` query string (currently trusted, audit-confirmed as a security gap — see API audit). The new design:

1. **Payload carries `ProjectId` as a first-class field**, not a JSON round-trip.
2. **Subscriber is responsible for its own scope** — `IssueGrain` only cares about `e.WorkflowRunId == _issue.ActiveWorkflowRunId`, not project. Project scoping is for the **Web fan-out** path.
3. **EventBridge** is the only component that needs project scoping (it's the Web fan-out point). It reads `IProjectScoped.ProjectId` from the payload and routes to that group. Empty/null → `project:global` (default).
4. **Auth on SignalR** (separate P0) — `MohistHub` should validate the principal's project access before joining the group. Out of scope for this design; covered in the API audit.

## Failure Modes

| Failure | Detection | Recovery |
|---------|-----------|----------|
| Handler throws | `InMemoryEventBus` catches per-handler | Log at error level + write to `DeadLetter` table; do not retry automatically |
| Subscriber grain deactivated at emit time | Bus has no subscriber registered | Subscriber must reconcile lazily on next activation by querying the latest `WorkflowRunRow.State` and reconciling (see "Lost event recovery" below) |
| Silo restart | `InMemoryEventBus` resets | **Durability** requirement: events must be in the persisted outbox before the bus emit returns; on restart, replay from outbox where watermark < now |
| Cross-grain call from handler blocks | `InMemoryEventBus` is sync-in-emit-thread | Handlers must be async-fire-and-forget; emit thread returns immediately; handler runs as a background task; the emit's ordering guarantee is per-handler, not cross-handler |
| Two events for same workflow arrive out of order | Possible if handler A is slow | Persisted outbox + subscriber watermark guarantee per-subscriber ordering; the bus itself is unordered |
| Project id mismatch in payload | Subscriber filter rejects; EventBridge routes to no group (or `project:global` fallback) | Log; investigate payload shape drift |

## Lost Event Recovery

Subscribers can be deactivated. Even with a durable outbox, a `WorkflowRunCompleted` could be emitted while `IssueGrain` is not activated, and re-activation may race the next emit. The "stuck issue" scenario today is exactly this: the hook was called and missed.

**Recovery mechanism: lazy reconciliation on read.**

`IssueGrain.GetWorkflowStatusAsync` already calls `_workflowQuerier.GetStatusAsync(wrId)` and projects workflow state (`IssueGrain.cs:214-235`). The projection in `MohistDefaultWorkflowProjection.ProjectWorkflowState` (`MohistDefaultWorkflowProjection.cs:28-65`) is extended to detect:

- `workflow.Status == "Completed"` and `issueStatus == "InProgress"` → return `WorkflowReconciliation.NeedsCompletion(wrId)`
- `workflow.Status == "Failed" | "Stopped"` and `issueStatus == "InProgress"` → return `WorkflowReconciliation.NeedsAbort(wrId, reason)`

`IssueGrain.GetWorkflowStatusAsync` then issues the appropriate command (`CompleteWorkAsync` / `AbortWorkAsync`) before returning. This is the same code path the bus-driven handler would take, just triggered by a read.

**No scheduled job needed** for the common case — every read of the issue reconciles. A daily `IHostedService` walks `Issue.Status == InProgress` issues with non-null `ActiveWorkflowRunId` and reconciles them (catches issues nobody opens).

## Migration Plan

Each step is independently shippable with a test.

### Step 1 — Adopt CloudEvents 1.0.2 envelope (no behavior change)

1. Add `CloudNative.CloudEvents` NuGet package to `Mohist.Server.csproj`.
2. New `CloudEventFactory` static class — constructs `CloudEvent` envelope with all required attributes (`id`, `source`, `specversion`, `type`) plus our standard extensions (`projectid`, `workflowrunid`, `issueno`).
3. Update `IEventBus` interface: `Emit(CloudEvent)` and `On(string type, Action<CloudEvent> handler)`. Keep a deprecated `Emit(string name, object payload)` overload for the 9 existing call sites during the migration window.
4. Update `WorkflowRunStore.Publish` to construct CloudEvents for the 18 workflow events using `CloudEventFactory.Create("com.mohist.workflow.run.completed", source, data, subject, extensions)`.
5. Update the 9 existing `Emit` call sites (`AgentSessionGrain.cs:284,301,311,324,345,354-358` and `WorkflowGrain.cs:892`) to construct CloudEvents.
6. Add `EventCatalog` (read-only inventory) with entries for all 27+ `type` values we actually emit.
7. Update `EventBridge` to forward CloudEvent JSON via SignalR (use `JsonEventFormatter.EncodeStructuredModeMessage`).
8. Add `EventCatalogTests`: every emit uses a `type` registered in the catalog, every catalog entry has at least one emit site.
9. Update `LiveTaskProvider` to use `@cloudevents/sdk-typescript` parser; subscribe to both new reverse-DNS `type` values AND legacy snake_case names for back-compat.

**Result:** All emits produce spec-compliant CloudEvent envelopes with `id`/`source`/`time`/extensions. The 18 currently-dropped events now reach the Web. No cross-grain behavior change. Wire format becomes the CloudEvents 1.0.2 JSON envelope, consumable by any standard tooling.

**Risk:** `id`/`source` not present on any event today; introducing them changes the wire shape — Web must update to parse the envelope before this can ship. Migrate Web in the same PR.

### Step 2 — Wire `WorkflowRunFailed` and `WorkflowRunStopped` to the existing hook chain (smallest fix for G1, G2)

1. Extend `IWorkflowCompletionHook` to `IWorkflowLifecycleHook` with `OnCompleted`, `OnFailed`, `OnStopped` methods.
2. In `WorkflowGrain.On(...)` (line 920-941), replace the no-op `WorkflowRunFailed => DisableWorkHeartbeatAsync()` with `OnWorkflowFailedAsync(reason) → DispatchLifecycleHooksAsync("failed", reason)`.
3. Replace the `WorkflowRunStopped => OnWorkflowStoppedAsync(reason)` body to also call `DispatchLifecycleHooksAsync("stopped", reason)`.
4. In `IssueWorkflowCompletionHook`, implement `OnFailed` and `OnStopped` → call `IIssueGrain.AbortWorkAsync`.
5. Add `IIssueGrain.AbortWorkAsync(wrId, reason)` and `Issue.AbortWorkflow(wrId, reason)` (with `Status == InProgress && wrId == _activeWorkflowRunId` guard).
6. Clear `_activeWorkflowRunId` in `Issue.Complete` and `Issue.Close` (fixes G3).
7. Add `IssueAbortSpecs` for the new transition; `IssueWorkflowCompletionSpecs` for the failure/stopped hook call.

**Result:** Failed/Stopped workflow transitions the issue to Cancelled. ActiveWorkflowRunId is cleared. Issue can be reopened or a new workflow started.

**Risk:** `CancelAsync` was triggering `StopAsync` + direct `_issue.Close()`. With the new hook chain, both `CancelAsync` and `StopAsync` cascade through the hook. Make `AbortWorkflow` idempotent in domain.

### Step 3 — Add `runner_disconnected` event (closes the runner-dies audit gap)

1. New event `RunnerDisconnectedEvent { runnerId, reason, disconnectedAt }` registered in `EventBusEventTypes.All`.
2. `_eventBus.Emit("runner_disconnected", ...)` in `RunnerHub.OnDisconnectedAsync` and `RunnerGrain.HandleTimeoutAsync`.
3. New `IAgentSessionGrain.FailIfRunningAsync(reason)` is **already defined** at `AgentSessionGrain.cs:232` (audit-confirmed; only used by tests today).
4. New `IHostedService` `AgentSessionRunnerBridge` subscribes to `runner_disconnected`; queries `AgentSessionRow` where `RunnerId == e.RunnerId` and `Status in (Running, Probing)`; calls `grain.FailIfRunningAsync($"runner-disconnected:{e.Reason}")`.
5. The same bridge also subscribes to `coder_session_failed` and `task_failed` for the symmetric path.

**Result:** Within 2 minutes of a runner dying, all its sessions are marked failed. Within seconds of an explicit disconnect. The "session stuck running" audit gap closes.

**Risk:** `HandleTimeoutAsync` already runs on a 2-minute timer; the cascade is asynchronous, so the workflow grain may not see the failure immediately. Add `WorkflowGrain` subscription to `runner_disconnected` that clears the lease and marks task failed directly.

### Step 4 — Move `IWorkflowCompletionHook` from completion-only to lifecycle (foundation for Step 5)

1. Rename `IWorkflowCompletionHook` to `IWorkflowLifecycleHook`; add `OnFailed` and `OnStopped` methods.
2. Migrate `IssueWorkflowCompletionHook` to implement all three.
3. Add a generic `WorkflowLifecycleBridge` that subscribes to `workflow_completed` / `workflow_failed` / `workflow_stopped` from the bus and calls the hook list — this is the same behavior as the existing in-grain dispatch, but reachable from any source.
4. The `WorkflowGrain` keeps calling the in-grain dispatch for now; the bridge is added as a parallel path that's used by tests to verify the subscription model works.

**Result:** Hook service can be moved off the grain. Foundation for Step 5.

### Step 5 — Subscribe `IssueGrain` to bus events (the canonical event-driven path)

1. `IssueGrain` registers subscriptions in `OnActivateAsync` for `workflow_completed`, `workflow_stopped`, `workflow_failed`, `stage_approval_requested`.
2. Each handler does the **filter by run id** check and calls a command (`CompleteWorkAsync` / `AbortWorkAsync`).
3. The in-grain `IWorkflowCompletionHook` dispatch in `WorkflowGrain` is **removed** for the Issue side; the worktree cleanup moves to a new `WorktreeCleanupHook : IWorkflowCompletedHook` (or stays in `IssueWorkflowCompletionHook` if simpler).
4. `IssueGrain.OnDeactivateAsync` disposes subscriptions.
5. Add a test fixture `IssueEventSubscriptionSpecs` that simulates emit → Issue transitions, with re-activation scenarios.

**Result:** IssueGrain reacts to workflow events without WorkflowGrain knowing about IssueGrain. The "issue 根据工作流完成事件把自己 done 掉" path is real.

**Risk:** Lost events if IssueGrain is deactivated at emit time. Mitigated by Step 6 (lazy reconciliation) — any read of the issue self-heals.

### Step 6 — Lazy reconciliation + daily hosted service

1. Extend `MohistDefaultWorkflowProjection.ProjectWorkflowState` to return `WorkflowReconciliation` markers.
2. `IssueGrain.GetWorkflowStatusAsync` consumes the marker and issues the command.
3. New `IHostedService` `IssueWorkflowReconciliationService` runs once a day, scans `Issue.Status == InProgress` rows with non-null `ActiveWorkflowRunId`, and calls `GetWorkflowStatusAsync` to trigger reconciliation.

**Result:** Even with the bus being in-process, the issue eventually catches up to workflow state. The "stuck" audit gap fully closes.

### Step 7 — Persisted outbox + replay

1. New `Outbox` table: `(Id, EventName, Payload, ProjectId, CommittedAt)`.
2. `IEventBus.Emit` synchronously writes to the outbox before fanning out.
3. Each subscriber maintains a `LastProcessedEventId` row.
4. On silo startup, `OutboxReplayService` replays all events with `CommittedAt > min(subscriber watermarks)`.
5. Subscribers can be restarted independently without losing events.

**Result:** Bus is now durable. The "silo restart drops events" failure mode is gone. Foundation for horizontal scaling (multiple silos in the future) if Mohist ever needs it.

**Risk:** Outbox writes add latency to every emit. For the current single-silo deployment this is acceptable (SQLite single-writer). Postgres deployment has a small extra round-trip per emit.

### Step 8 — Replace `IWorkflowCompletionHook` with bus-driven reactions (clean break)

1. Remove the in-grain hook dispatch from `WorkflowGrain`.
2. The only consumer of workflow events is the bus.
3. `WorktreeCleanupHook` subscribes to `workflow_completed` from the bus.
4. `IssueGrain` subscribes to lifecycle events from the bus (Step 5).

**Result:** WorkflowGrain no longer has any reference to IssueGrain. The boundary "domain decides, subscribers react" is the only path.

### Step 9 — Wire up Runner / Session symmetry

1. `WorkflowGrain` subscribes to `coder_session_failed` and `task_failed` — if the event's `WorkflowRunId` matches, treat as the runner report arrived with a failure and process the same way.
2. `AgentSessionGrain` subscribes to `workflow_completed` / `workflow_failed` / `workflow_stopped` — if the session's `WorkflowRunId` matches, mark terminal.
3. `runner_dispatched` event drives `AgentSessionGrain` pre-allocation.
4. `lease_expired` event from `WorkflowGrain` heartbeat reminder drives the stuck-lease recovery.

**Result:** End-to-end event-driven. No synchronous cross-grain coordination except where Orleans forces it (locking, claim acquisition).

### Step 10 — TS runner per-session spawn (separate workstream)

1. Move `SharedAcpConnection` and `AcpSessionManager` to `RunnerHost` lifetime.
2. TS runner emits `runner_acp_session_started` / `_terminated` / `_crashed` so the server can fan out.
3. Server-side hub method pushes `workflow_cancelled` to the runner, which cancels the active ACP session.

**Result:** Cross-task state preservation. Runner doesn't lose context between tasks.

## Test Surface

New tests required:

| File | Coverage |
|------|----------|
| `Specs/Foundation/EventBusSpecs.cs` (extend) | Every name in `EventBusEventTypes.All` reaches a subscriber. Per-handler exception does not break dispatch. Unsubscribe stops delivery. |
| `Specs/Foundation/OutboxReplaySpecs.cs` (new) | Replay from outbox; subscriber watermark advances; out-of-order events handled. |
| `Specs/Workflow/Grain/WorkflowEventBusSpecs.cs` (new) | `WorkflowGrain.StartAsync` emits `workflow_started` + `stage_started`. `ApproveAsync` emits `stage_approval_resolved` + `stage_completed`. `RerunAsync` emits `workflow_rerunning`. |
| `Specs/Issue/Grain/IssueEventSubscriptionSpecs.cs` (new) | `IssueGrain` subscribes in `OnActivateAsync`. `OnWorkflowCompleted` triggers `CompleteWorkAsync`. `OnWorkflowFailed` triggers `AbortWorkAsync`. Re-activation re-subscribes. Lost event at deactivate is recovered on next read. |
| `Specs/Issue/Domain/IssueAbortSpecs.cs` (new) | `Issue.AbortWorkflow(wrId, reason)` is idempotent. `Close` clears `_activeWorkflowRunId`. |
| `Specs/Issue/Domain/IssueReopenSpecs.cs` (extend) | After Failed, `Reopen` + `StartWorkAsync` succeeds (G3 closed). |
| `Specs/Runner/Grain/RunnerDisconnectSpecs.cs` (new) | `RunnerGrain.HandleTimeoutAsync` emits `runner_disconnected`. `RunnerHub.OnDisconnectedAsync` emits `runner_disconnected`. `AgentSessionGrain` for the disconnected runner is marked failed within 5s. |
| `Specs/Workflow/Grain/WorkflowFailedCompletionSpecs.cs` (new) | End-to-end: task fails → `task_failed` → `workflow_failed` → `IssueWorkflowCompletionHook.OnFailedAsync` → Issue transitions to `Cancelled`. |

End-to-end integration:

| Test | Coverage |
|------|----------|
| `Specs/Integration/IssueWorkflowEventLoopSpecs.cs` (new) | Full scenario: user creates issue → starts workflow → agent runs task → user approves → integrate runs → workflow completes → issue transitions to Done. The "issue根据工作流完成事件把自己done掉" assertion. |

## Open Questions

1. **Project scoping on `MohistHub`**: do we trust the `?projectId=` query string until auth is added? (API audit says no.) This design assumes the auth fix is parallel.
2. **CloudEvent JSON Schema as the contract**: do we publish `dataschema` URIs and JSON Schema files for each `type`? The CloudEvents spec recommends this for self-describing events. Out of scope here, but `EventCatalog` has a `DataSchema` field ready for it.
3. **Outbox table location**: same SQLite/Postgres database, or a separate one? Single DB is simpler; separate allows future Redis/Kafka migration. With CloudEvents compliance, the bus is portable — moving to Kafka later requires only a transport binding.
4. **TS runner per-session spawn**: Steps 9-10 interact with the runner's TS code, which is a separate package. This design covers the bus/host side; the TS implementation is in `packages/runner/`.
5. **How to express "approval → review → merge → done" in the new design**: Per the design history, the user wants a `Review` sub-state between approval and merge. The new design supports this naturally — the `Review` sub-state is a value on `WorkflowRun` (e.g. `StageRunStatus.Review` or a synthetic stage `merge` with `requiresApproval: true`). The events are unchanged; the state machine just has more states. The yaml can be edited; the design accommodates.
6. **Backwards compat for legacy `type` names**: 36 legacy snake_case names (`coder_session_failed`, `stage_changed`, etc.) are valid CloudEvents `type` values. We keep them as alias constants on the server (the bridge translates the catalog entry's reverse-DNS name to a legacy alias if needed for old Web clients). New emits go out under reverse-DNS only. Web gradually migrates to reverse-DNS.

## Summary

This design replaces the current "in-grain switch + 36 dead registrations" with a clear, durable, event-driven coordination model:

- **Origin**: domain events, named via `snake_case`, with payload DTOs.
- **Bus**: in-process, with a durable outbox; named via a canonical catalog.
- **Subscribers**: grains (IssueGrain, AgentSessionGrain, WorktreeCleanupHook) and the Web via SignalR.
- **Reactions**: always call commands, never mutate other aggregates.
- **Recovery**: lazy reconciliation on read + daily hosted service.
- **Migration**: 10 steps, each independently shippable. The first three (canonicalization, failure/stopped hook, runner_disconnected) close 5 of the 6 P0 gaps.

The "issue 根据工作流完成事件把自己 done 掉" path is the canonical example: `WorkflowGrain` mutates state → `WorkflowRun.Complete()` returns the fact → `WorkflowGrain.CommitAsync` emits `workflow_completed` → `IssueGrain.OnWorkflowCompleted` (subscriber) calls `CompleteWorkAsync` → `Issue.Complete` (domain) validates and transitions → `IssueGrain` emits `issue_completed` → Web kanban moves the card.
