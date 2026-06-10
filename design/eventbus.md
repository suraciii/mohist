---
purpose: "Define the in-process event bus. CloudEvents 1.0.2 envelopes. Producer publishes domain events produced by domain methods; metadata is literal strings passed at the call site. Bus routes by CloudEvent.Type to statically-registered handlers."
include:
  - "Module definitions (interfaces)."
  - "Module capabilities (interface methods)."
  - "Module interaction diagrams."
  - "Handler shape and subscription model."
  - "Migration path."
exclude:
  - "Implementation details (delegate weaving, reflection)."
  - "WorkflowRun state machine; see workflow-domain-events.md."
  - "Issue / AgentSession / Runner reaction logic; see design/<bounded-context>.md."
  - "HTTP API contract; see design/architecture.md."
---

# Event Bus

## 模块

### 1. IEventPublisher

**职责**：发送事件。

```csharp
public interface IEventPublisher
{
    Task PublishAsync<TData>(
        TData data,
        string type,
        string source,
        string? subject = null,
        IReadOnlyDictionary<string, string>? extensions = null,
        CancellationToken ct = default) where TData : class;
}
```

### 2. IEventSubscriber

**职责**：注册 handler，冻结订阅表。

```csharp
public interface IEventSubscriber
{
    void RegisterHandler(object handler);
    void Freeze();
}
```

### 3. ICloudEventHandler / ICloudEventHandler<TData>

**职责**：事件处理接口。两条独立路径——handler 实现其中一个。

```csharp
public interface ICloudEventHandler<TData> where TData : class
{
    bool Filter(CloudEvent<TData> evt);
    Task HandleAsync(CloudEvent<TData> evt, CancellationToken ct);
}

public interface ICloudEventHandler
{
    bool Filter(CloudEvent evt);
    Task HandleAsync(CloudEvent evt, CancellationToken ct);
}
```

### 5. SubscriptionAttribute

**职责**：声明 handler 订阅的 event type。所有 handler 必须标注。

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class SubscriptionAttribute : Attribute
{
    public required string Type { get; init; }
}
```

**`Type` 字符串语法**：
- 字面精确：`com.mohist.workflow.run.completed`
- 段通配：`com.mohist.workflow.*`（匹配 `com.mohist.workflow.X` 任意 X）
- 全通配：`*`
- `|` 分隔：`a|b|c` —— 同一 handler 订阅多个 type

中间通配符（`foo.*.bar`）禁止。

`Subscription` 是统一记录——`Type` 字段承载字面、通配或 `|`-分隔多值：

```csharp
public sealed record Subscription(
    string Type,
    object Handler,
    DispatchDelegate Dispatch);
```

### 6. CloudEvent / CloudEvent<TData>

**职责**：CloudEvents 1.0.2 信封。

`CloudEvent` 是统一形态（`Data: JsonElement?`），handler 通过两种接口消费：

```csharp
public sealed class CloudEvent
{
    public string Id { get; }
    public Uri Source { get; }
    public string Type { get; }
    public DateTimeOffset Time { get; }
    public JsonElement? Data { get; }
    public string? DataContentType { get; }
    public string? Subject { get; }
    public string SpecVersion { get; }
    public IReadOnlyDictionary<string, string> Extensions { get; }
}

public sealed class CloudEvent<TData> where TData : class
{
    public string Id { get; }
    public Uri Source { get; }
    public string Type { get; }
    public DateTimeOffset Time { get; }
    public TData Data { get; }
    public string? DataContentType { get; }
    public string? Subject { get; }
    public string SpecVersion { get; }
    public IReadOnlyDictionary<string, string> Extensions { get; }
}
```

`CloudEvent<TData>` 是强类型视图，bus 在 dispatch 时反序列化 `Data` JSON 构造。

## 模块交互

### 交互 1：Producer → Bus

```
┌─────────────┐     PublishAsync(payload, type, source, ...)      ┌─────────────┐
│   Grain     │ ────────────────────────────────────────────────> │  IEvent     │
│ (Workflow,  │                                                  │  Publisher  │
│  Issue...)  │                                                  │             │
└─────────────┘                                                  └─────────────┘
```

### 交互 2：Bus → Handler

```
┌─────────────┐     CloudEvent<TData>                             ┌─────────────┐
│  IEvent     │ ────────────────────────────────────────────────> │ ICloudEvent │
│  Publisher  │                                                  │ Handler<T>  │
│             │     CloudEvent                                    │             │
│             │ ────────────────────────────────────────────────> │ ICloudEvent │
│             │                                                  │  Handler    │
└─────────────┘                                                  └─────────────┘
```

### 交互 3：DI 注册 → Bus 构造 → Handler

```
┌─────────────────┐
│   Program.cs    │
│  services.Add   │
│  CloudEventBus  │
└────────┬────────┘
         │
         ▼
┌─────────────────┐     扫描 assembly 中所有        ┌─────────────────────────┐
│ AddCloudEvent   │ ──> 实现 ICloudEventHandler 的  │ CloudEventBusConfiguration│
│ HandlersFrom    │     类，检查 [Subscription]      │ (type → handler types)   │
│ Assembly()      │                                  └────────────┬────────────┘
└─────────────────┘                                               │
                                                                  │ 注入
                                                                  ▼
┌─────────────────────────────────────────────────────────────────────────────────┐
│                              InMemoryEventBus                                    │
│  ┌─────────────────────────────────────────────────────────────────────────────┐ │
│  │ 构造阶段：                                                                  │ │
│  │ 1. 从 CloudEventBusConfiguration 读取所有 (type, handlerType)               │ │
│  │ 2. 从 DI 获取 handler instance                                              │ │
│  │ 3. 调用 RegisterHandler(handler)                                            │ │
│  │ 4. Freeze() → FrozenDictionary<string, List<Subscription>>               │ │
│  └─────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                  │
│  implement IEventPublisher + IEventSubscriber                                    │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### 交互 4：Publish → Route → Dispatch

```
PublishAsync<TData>(payload, type, source, ...)
   │
   ▼
┌─────────────────────┐
│ 1. Build CloudEvent │  { id, type, source, time, data=JsonElement, ... }
│    envelope         │
└──────────┬──────────┘
           │
           ▼
┌─────────────────────┐
│ 2. Lookup           │  frozenDict.TryGetValue(evt.Type)
│    frozenDict[type] │  → List<Subscription>
└──────────┬──────────┘
           │
           ▼
┌─────────────────────┐
│ 3. For each         │  foreach subscription in list:
│    subscription     │    if (!handler.Filter(evt)) continue
│                     │    await dispatch(handler, evt, ct)
└─────────────────────┘
```

### 交互 5：Handler 订阅声明

```
┌─────────────────────────────┐
│ [Subscription(Types = [...])]│
└─────────────┬───────────────┘
              │
              ▼
┌─────────────────────────────┐
│     ICloudEventHandler      │  或 ICloudEventHandler<TData>
│     Filter(CloudEvent)      │
└─────────────┬───────────────┘
              │
              ▼
┌─────────────────────────────┐     ┌─────────────────────────────┐
│  ICloudEventHandler<TData>  │ ──> │ HandleAsync(CloudEvent<T>)   │  (强类型)
└─────────────────────────────┘     └─────────────────────────────┘
              │
              ▼
┌─────────────────────────────┐     ┌─────────────────────────────┐
│     ICloudEventHandler      │ ──> │ HandleAsync(CloudEvent)      │  (动态)
│     (直接实现)              │     │ (类方法，非接口)             │
└─────────────────────────────┘     └─────────────────────────────┘
```

## Handler Shape

```csharp
// 强类型 — 拿 CloudEvent<TData>（bus 在 dispatch 时反序列化）
[Subscription(Type = "com.mohist.workflow.run.completed")]
public sealed class WorktreeCleanupService : ICloudEventHandler<WorkflowRunCompleted>
{
    public bool Filter(CloudEvent<WorkflowRunCompleted> evt) => true;
    public Task HandleAsync(CloudEvent<WorkflowRunCompleted> evt, CancellationToken ct) { ... }
}

// 动态 — 拿 CloudEvent，handler 自己解析 Data
[Subscription(Type = "com.mohist.*")]
public sealed class EventBridge : ICloudEventHandler
{
    public bool Filter(CloudEvent evt) => true;
    public Task HandleAsync(CloudEvent evt, CancellationToken ct) { ... }
}
```

## 目标

| 维度 | 决策 |
|------|------|
| **Event 形态** | CloudEvents 1.0.2 envelope |
| **Domain event** | 领域方法返回 |
| **Type 字符串** | 字面量 reverse-DNS，无中间表 |
| **Source/Subject/Extensions** | 字面量，grain 端 C# string interpolation 拼好 |
| **Producer API** | `IEventPublisher.PublishAsync<TData>` |
| **Handler 订阅** | `[Subscription(Types = [...])]` + frozen dict |
| **Handler 入参** | `CloudEvent<TData>` 或 `CloudEvent` |
| **Handler 过滤** | `Filter(CloudEvent)` |
| **订阅时机** | DI 阶段扫描，构造时 freeze |
| **路由** | O(N) pattern 匹配（N = 总 subscription 数） |
| **持久化** | 无（in-memory + lazy reconciliation） |

## Publish-After-Commit

```
Grain.CommitAsync(events)
   │
   ├─ store.SaveAsync(run, events)   ← persist
   │     ├─ db.SaveChanges
   │     └─ transaction.Commit
   │
   └─ foreach (evt, type) in events:
        await _publisher.PublishAsync(evt, type, ...)  ← commit 之后调
```

## Producer 用法

```csharp
// Grain 或 store 的 commit 之后
private async Task PublishAsync(IReadOnlyList<WorkflowEvent> events, CancellationToken ct)
{
    foreach (var evt in events) switch (evt)
    {
        case WorkflowRunStopped e:
            await _publisher.PublishAsync(e,
                type:      "com.mohist.workflow.run.stopped",
                source:    $"/mohist/workflow/{runId}",
                subject:   e.IssueNumber.ToString(),
                extensions: new Dictionary<string, string>
                {
                    ["projectid"] = e.ProjectId,
                    ["reason"]    = e.Reason,
                },
                ct);
            break;
    }
}
```
            await _publisher.PublishAsync(e,
                type:      "com.mohist.workflow.run.stopped",
                source:    $"/mohist/workflow/{e.WorkflowRunId}",
                subject:   e.IssueNumber.ToString(),
                extensions: new Dictionary<string, string>
                {
                    ["projectid"] = e.ProjectId,
                    ["reason"]    = e.Reason,
                },
                ct);
            break;
    }
}
```

## Migration

1. 定义 `IEventPublisher`, `IEventSubscriber`, `ICloudEventHandler`, `ICloudEventHandler<TData>`, `CloudEvent<TData>`, `SubscriptionAttribute`。
2. 定义 payload record（纯 data，无 attribute）。
3. Type 字符串：producer / consumer 各写字面量。无 EventCatalog。
4. 重写 handler：加 `[Subscription]`，implement `ICloudEventHandler` 或 `ICloudEventHandler<TData>`。
5. 实现 `InMemoryEventBus`（同时 implement `IEventPublisher` 和 `IEventSubscriber`）。
6. Producer 迁移：13 处旧 publish → `bus.PublishAsync(payload, type, source, subject?, extensions?, ct)`。
7. EventBridge 改为 `ICloudEventHandler` 多 type 订阅。

## Related

- `design/architecture.md`
- `design/workflow-scheduling.md`
- `design/issue-workflow-coordination.md`
