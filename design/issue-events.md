# Issue 领域事件

## Goal

为 Issue 聚合根引入 13 个领域事件变体，让 Issue 的状态变化在 CloudEvents 1.0.2 框架下成为不可变事实记录，与 WorkflowEvent 共享同一套基础设施。

## Background

Issue 聚合根当前没有任何领域事件——状态变化只修改 `Issue` 实体的内部字段，外部上下文无法在不调用 grain 的情况下感知到"Issue X 在 T 时刻 Archived 了"或"Issue X 加了 prerequisite #42"。

`WorkflowRunStore.PublishAsync` 已经把 WorkflowEvent 持久化到 `WorkflowRunEvents` 表并通过 CloudEvent 总线发布。Issue 域缺少同等的"事实记录层"。

## Scope

**包含：**
- `IssueEvent` 域类型（C# 14 union pattern，13 个变体）
- Issue 聚合根收集 `pending events` 的内部机制
- `IssueEvents` 新表（结构镜像 `WorkflowRunEvents`）+ EF migration
- `IEventStore` 扩展支持按 issueId list（共享 `AppendAsync(CloudEvent)`）
- `IssueGrain` 在 save 后调 publish（publish-after-commit 严格语义）
- `GET /api/projects/{ref}/issues/{number}/events` 读端返回 issue 事件
- 删除 `IssueWorkflowAbortedHandler`（被新的 issue 事件替代）

**不包含：**
- Outbox relay / 失败重投（独立 issue 跟踪）
- CommentAdded 事件（产品决策不发，评论是子实体）
- TitleChanged / BodyChanged（纯文档编辑，无外部订阅者）
- `IssueArchived` 触发的 worktree 清理改写——清理仍在 `IssueRoutes.Lifecycle.cs` archive 路径显式调（issue #146）

## 事件清单

| CloudEvent Type | IssueEvent 变体 | 触发 Transition | 关键字段 |
|---|---|---|---|
| `com.mohist.issue.created` | `IssueCreated` | `Issue.Create()` | title, priority, labels, repositoryRef |
| `com.mohist.issue.labels-changed` | `IssueLabelsChanged` | `Issue.Update(labels)` | oldLabels, newLabels |
| `com.mohist.issue.priority-changed` | `IssuePriorityChanged` | `Issue.Update(priority)` | oldPriority, newPriority |
| `com.mohist.issue.prerequisite-added` | `IssuePrerequisiteAdded` | `Issue.AddPrerequisite(n)` | prerequisiteNumber |
| `com.mohist.issue.prerequisite-removed` | `IssuePrerequisiteRemoved` | `Issue.RemovePrerequisite(n)` | prerequisiteNumber |
| `com.mohist.issue.work-started` | `IssueWorkStarted` | `Issue.StartWorkflow(wrId)` | workflowRunId |
| `com.mohist.issue.work-completed` | `IssueWorkCompleted` | `Issue.Complete(wrId)` | workflowRunId |
| `com.mohist.issue.work-aborted` | `IssueWorkAborted` | `Issue.AbortWorkflow(wrId, reason)` | workflowRunId, reason |
| `com.mohist.issue.closed` | `IssueClosed` | `Issue.Close(reason)` | reason |
| `com.mohist.issue.archived` | `IssueArchived` | `Issue.Archive()` | (公共字段) |
| `com.mohist.issue.unarchived` | `IssueUnarchived` | `Issue.Unarchive()` | (公共字段) |
| `com.mohist.issue.reopened` | `IssueReopened` | `Issue.Reopen()` | (公共字段) |

**实际变体数：12**。`IssueRepositoryRefChanged` 不在表中——`Issue.RepositoryRef` 只能 `init`（不可变），当前没有 transition 触发它；按"不预创死代码"原则删去。

**语义区分**：`WorkAborted` 与 `Closed` 都会把 status 推到 `Cancelled`，但语义不同：
- `WorkAborted` 来自 `InProgress`（workflow 失败 / 停止导致）
- `Closed` 来自 `Backlog`（用户主动关掉没跑的 issue）

外部订阅者通过事件**类型**区分，不依赖 status 字段。

## Source URI

```
/mohist/issues/{issueId}
```

- context = `mohist`
- aggregate = `issues`
- id = issue id (例如 `issue_abc123`)

与 `WorkflowRunEvents` 共享 `WorkflowRunEventPersistence.SourcePrefix` 常量模式：
```csharp
internal static class IssueEventPersistence
{
    public const string SourcePrefix = "/mohist/issues/";
    public static string IssueSource(string issueId) => $"{SourcePrefix}{issueId}";
}
```

## Module 设计

### Module 1: IssueEvent 域

```
Issue/Domain/Events/IssueEvent.cs
```

C# 14 union pattern，与 `WorkflowEvent` 一致风格：

```csharp
public abstract record IssueEvent(
    string IssueId,
    int IssueNumber,
    string ProjectId,
    DateTimeOffset OccurredAt)
{
    public sealed record Created(...) : IssueEvent;
    public sealed record LabelsChanged(string[] OldLabels, string[] NewLabels) : IssueEvent;
    // ... 11 more
}
```

### Module 2: 聚合根收集

```
Issue/Domain/Issue.cs (扩展)
Issue/Domain/Issue.Transitions.cs (改造)
```

```csharp
public sealed partial class Issue
{
    private readonly List<IssueEvent> _pendingEvents = new();
    public IReadOnlyList<IssueEvent> PendingEvents => _pendingEvents;
    public void ClearPendingEvents() => _pendingEvents.Clear();
    private void RecordEvent(IssueEvent evt) => _pendingEvents.Add(evt);
}
```

**不变式：**
- `_pendingEvents` 只在 state 修改**之后**追加
- transition 失败抛异常 → 不追加事件（事件是"已发生"，不是"将要发生"）
- 事件本身**不抛异常**（构造事件是纯数据操作）

### Module 3: IssueEvents 表

```
Infrastructure/Data/Issue/IssueEventRow.cs
Infrastructure/Data/Migrations/20260610XXXXXX_AddIssueEvents.cs
```

| Column | Type | Notes |
|---|---|---|
| `Source` | TEXT NOT NULL | `/mohist/issues/{id}` |
| `Id` | INTEGER NOT NULL | per-source sequence, monotonic |
| `EventId` | TEXT NOT NULL UNIQUE | CloudEvents 1.0.2 id (globally unique) |
| `Type` | TEXT NOT NULL | `com.mohist.issue.*` |
| `SpecVersion` | TEXT NOT NULL | `"1.0"` |
| `Time` | TEXT NOT NULL | ISO 8601 |
| `Subject` | TEXT NULL | issue number |
| `DataContentType` | TEXT NULL | `application/json` |
| `Data` | TEXT NULL | serialized `IssueEvent` JSON |
| `ExtensionsJson` | TEXT NULL | `{projectid, issueno, issueid}` |

PK: `(Source, Id)`. Index: `(Type, Source, Id)`.

镜像 `WorkflowRunEvents` 结构，但 source URI prefix 区分 aggregate。

### Module 4: IEventStore 扩展

```
Infrastructure/Events/IEventStore.cs
Infrastructure/Data/Events/EventStore.cs
```

`AppendAsync(CloudEvent envelope)` 已是 envelope-first，无需改。

新增读端：
```csharp
Task<IReadOnlyList<StoredCloudEvent>> ListIssueEventsAsync(
    string issueId, int limit = 200, CancellationToken ct = default);
```

实现按 `Source == IssueEventPersistence.IssueSource(issueId)` 过滤。

### Module 5: IssueGrain publish-after-commit

```
Issue/Grains/IssueGrain.cs
```

```csharp
private async Task SaveIssueAsync()
{
    if (_issue is null) return;
    var pending = _issue.PendingEvents;
    _issue.ClearPendingEvents();
    await _issueStore.SaveAsync(_issue.Id, _issue);
    await PublishIssueEventsAsync(pending);
}

private async Task PublishIssueEventsAsync(IReadOnlyList<IssueEvent> events)
{
    try
    {
        foreach (var evt in events)
        {
            var envelope = IssueEventEnvelope.From(evt);
            await _eventStore.AppendAsync(envelope);
            await _eventBus.PublishAsync(
                envelope.Data!, envelope.Type,
                envelope.Source.ToString(), envelope.Subject, envelope.Extensions);
        }
    }
    catch (Exception ex)
    {
        _log.LogError(ex, "Post-commit publish failed for issue {IssueId}; events lost", _issue?.Id);
    }
}
```

`_issueStore.SaveAsync` 和 `_eventStore.AppendAsync` 在独立的 try/catch 块，**DB commit 失败抛异常（已有）** vs **publish 失败仅记 LogError**。

### Module 6: 读端 API

```
Api/WorkflowEventRoutes.cs (扩展)
```

`GET /api/projects/{projectRef}/issues/{number}/events`：
- 合并返回 `IssueEvents`（按 issueId）+ `WorkflowRunEvents`（按 issue.WorkflowRunId）
- 按 `Envelope.Time` 排序返回全时间线
- Issue 视角：UI 看到"这个 issue 的所有事件"（issue lifecycle + workflow 内部过程事件）

`GET /api/workflow-runs/{workflowRunId}/events` 保持原 workflow 域语义。

### Module 7: 删 IssueWorkflowAbortedHandler

```
Issue/Services/WorkflowProfiles/IssueWorkflowAbortedHandler.cs
```

**删除整个文件**——`com.mohist.workflow.run.stopped` / `.failed` handler 价值归零：
- issue 状态机已通过 grain method 同步完成 abort
- 新的 `com.mohist.issue.work-aborted` 事件**自动**被 `EventBridge` 通配 `com.mohist.*` 转发到 Web UI

`EventBridge` 无需改。

## 流程图

### Issue transition → 事件发布

```
User HTTP POST /api/issues/{n}/start
    │
    ▼
IssueGrain.StartWorkAsync
    │
    ├── 1. _issue.StartWorkflow(wrId)
    │       │
    │       ├── 校验不变式
    │       ├── 修改 _status / _activeWorkflowRunId
    │       └── RecordEvent(IssueEvent.WorkStarted(wrId))
    │
    ├── 2. WorkflowGrain.StartAsync (同步调用)
    │
    ├── 3. SaveIssueAsync
    │       ├── _issueStore.SaveAsync (commit)
    │       └── PublishIssueEventsAsync(pending)
    │               ├── EventStore.AppendAsync (envelope → IssueEvents)
    │               └── IEventPublisher.PublishAsync
    │                       │
    │                       ▼
    │                  EventBridge (com.mohist.* wildcard)
    │                       │
    │                       ▼
    │                  SignalR Hub → Web UI
    │
    └── 4. 返回 wrId
```

### Issue 事件订阅

```
com.mohist.issue.* events published
    │
    ▼
InMemoryEventBus.Matches("com.mohist.*", "com.mohist.issue.work-started")
    │
    ▼
EventBridge.HandleAsync(envelope)
    │
    ▼
UserNotificationDispatcher.ResolveTargetConnections
    │
    ▼
SignalR Hub: Client.OnEvent("com.mohist.issue.work-started", envelope)
    │
    ▼
Web UI 收到事件，状态机更新
```

## 不变式

1. **事件是 past tense 事实**——只能从已成功的 transition 派生，失败抛异常时不发
2. **事件不可变**——构造后不修改，持久化即定论
3. **publish-after-commit**——`_issueStore.SaveAsync` 必须先 commit，再持久化 + 发布事件
4. **publish 失败不丢事实**——`IssueEvents` 表已持久化即事实存在；in-memory bus 通知失败仅记 LogError
5. **事件不携带"应该做什么"**——handler 决策，不在事件里

## 与 WorkflowEvent 对称性

| 维度 | Workflow | Issue |
|---|---|---|
| 域类型文件 | `Workflow/Domain/Run/WorkflowEvent.cs` | `Issue/Domain/Events/IssueEvent.cs` |
| 持久化表 | `WorkflowRunEvents` | `IssueEvents` |
| Source prefix | `/mohist/workflow-runs/` | `/mohist/issues/` |
| 收集位置 | `WorkflowRunStore.SaveAsync(run, events)` 参数 | `Issue._pendingEvents` 内部 list |
| Publish 调用 | `WorkflowRunStore.PublishAsync` | `IssueGrain.PublishIssueEventsAsync` |
| CloudEvent type | `com.mohist.workflow.*` | `com.mohist.issue.*` |
| Union 风格 | C# 14 abstract record + sealed | **同** |
| 读端 | `/api/workflow-runs/{id}/events` | `/api/issues/{id}/events` |

**完全对称**——同一套基础设施，同一种模式。
