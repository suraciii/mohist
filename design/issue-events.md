---
purpose: "Define the Issue domain event catalogue and the integration of Issue-side events with the existing in-process event bus. Issue becomes a peer event source alongside WorkflowRun; both share the same envelope, store, and bus."
include:
  - "Event catalogue (variants, type strings, semantic distinctions)."
  - "Module definitions (interfaces, contracts)."
  - "Module capabilities (methods)."
  - "Module interaction diagrams."
  - "Source URI convention."
exclude:
  - "Specific class names, file paths, method bodies."
  - "WorkflowRun / AgentSession event details; see eventbus.md and workflow-domain-events.md."
  - "EF migration specifics; see architecture.md."
  - "HTTP API contract; see architecture.md."
---

# Issue Domain Events

## 背景

Issue 聚合根当前没有任何领域事件。状态变化只修改实体内部字段，外部上下文无法在不调用聚合根持有者的情况下感知到"Issue X 在 T 时刻 Archived 了"或"Issue X 加了 prerequisite #42"。

WorkflowRun 已经有 17 个 union 变体走 CloudEvents 1.0.2 envelope 持久化 + 发布。Issue 域缺少同等的"事实记录层"。

**领域事件是模型的一部分**——它表达过去事实、不可变、对其他上下文/聚合有意义。事件不依赖事件驱动或异步分发；它是聚合根在 transition 之后**记录**的事实，未来用于审计、回放、跨聚合协调。

## 目标

为 Issue 聚合根引入领域事件变体，纳入与 WorkflowRun 共享的事件基础设施——同一套 envelope 格式、同一套 store、同一套 bus 路由。Issue 视角的读端 API 合并展示 issue 域事件与 workflow 域事件，让 UI 看到完整时间线。

## 事件清单

| CloudEvent Type | 变体 | 触发 Transition | 关键字段 |
|---|---|---|---|
| `com.mohist.issue.created` | `Created` | 新建 issue | title, priority, labels, repositoryRef |
| `com.mohist.issue.labels-changed` | `LabelsChanged` | 改 labels | oldLabels, newLabels |
| `com.mohist.issue.priority-changed` | `PriorityChanged` | 改 priority | oldPriority, newPriority |
| `com.mohist.issue.prerequisite-added` | `PrerequisiteAdded` | 加依赖 | prerequisiteNumber |
| `com.mohist.issue.prerequisite-removed` | `PrerequisiteRemoved` | 删依赖 | prerequisiteNumber |
| `com.mohist.issue.work-started` | `WorkStarted` | 启动 workflow | workflowRunId |
| `com.mohist.issue.work-completed` | `WorkCompleted` | workflow 完成 | workflowRunId |
| `com.mohist.issue.work-aborted` | `WorkAborted` | workflow 失败 / 停止 | workflowRunId, reason |
| `com.mohist.issue.closed` | `Closed` | 用户主动关闭 | reason |
| `com.mohist.issue.archived` | `Archived` | 归档 | (公共字段) |
| `com.mohist.issue.unarchived` | `Unarchived` | 取消归档 | (公共字段) |
| `com.mohist.issue.reopened` | `Reopened` | 重新打开 | (公共字段) |

**变体数 12**。`TitleChanged` / `BodyChanged` 不发（纯文档编辑，无外部订阅者）；`CommentAdded` 不发（评论是子实体，不是聚合根 transition）。`RepositoryRefChanged` 当前无 transition 触发（不可变字段）——按"不预创死代码"原则不引入。

**关键语义区分**：`WorkAborted` 与 `Closed` 都把 status 推到 `Cancelled`，但语义不同：
- `WorkAborted` 来自 `InProgress`（workflow 失败 / 停止导致）
- `Closed` 来自 `Backlog`（用户主动关掉没跑的 issue）

外部订阅者通过事件**类型**区分，**不**依赖 status 字段。

## Source URI

```
/mohist/issues/{issueId}
```

- context = `mohist`
- aggregate = `issues`
- id = issue id

与 workflow run 域的 `/mohist/workflow-runs/{runId}` 对称。

## 模块

### 1. IssueEvent 域类型

**职责**：定义 Issue 聚合根产生的 12 个事件变体。union pattern，与 WorkflowEvent 风格一致。

```
public union IssueEvent(...)
```

每个变体是不可变 record。事件是 past tense 事实，构造后不修改。

**关键不变量**：
- 事件是 past tense（`Created` / `Closed` / `Archived` 等已发生事实）
- 事件携带最小必要事实，**不**携带"应该做什么"
- transition 失败抛异常 → **不**追加事件（事件是"已发生"，不是"将要发生"）

### 2. 聚合根事件收集

**职责**：聚合根在 transition 中记录待发布事件。状态机与事件流是**同一 transition** 的两个面。

**接口**：
```
PendingEvents : IReadOnlyList<IssueEvent>  // 只读快照
ClearPendingEvents()                        // 持久化后清空
```

transition 在修改 state **之后**追加事件。事件构造是纯数据操作（不抛异常）。

### 3. 持久化模块

**职责**：把 CloudEvent 1.0.2 envelope 持久化到 issue 视角的事件存储。

**与 workflow 域对称**：
- Workflow run envelope 表：`WorkflowRunEvents`（PK: `Source+Id`, IX: `Type+Source+Id`）
- Issue envelope 表：`IssueEvents`（结构镜像）

两表**不合并**——issue 与 workflow 是不同 bounded context，分表让未来分库/分 schema 不留接缝。

**接口**（与 workflow 域共享 `IEventStore`）：
```
AppendAsync(envelope: CloudEvent) → Task
ListIssueEventsAsync(issueId, limit) → IReadOnlyList<StoredCloudEvent>
```

`AppendAsync` 按 source URI prefix 路由到对应表（`/mohist/issues/` 写 issue 表，`/mohist/workflow-runs/` 写 workflow 表）。

### 4. 事件序列化器

**职责**：union 变体 ↔ CloudEvent type 字符串 / envelope data 双向转换。

**接口**：
```
BusType(payload) → string         // reverse-DNS for bus
ToData(payload) → JsonElement     // envelope data payload
Unwrap(payload) → object          // union case extraction (for switch)
```

与 `WorkflowEventSerializer` 同结构。

### 5. 聚合根持有者的发布路径

**职责**：聚合根持有者 (grain) 在持久化 issue state **之后**发布事件，严格 publish-after-commit。

**交互**：
1. 持久化 issue state（commit 成功）
2. 取走 pending events
3. 逐个事件：构造 envelope → 持久化到 store → 发送到 bus
4. 持久化失败抛异常回退
5. 发布失败**仅**记 LogError（事件已写入 `IssueEvents` 表，事实已成立；bus 通知失败不丢事实）

### 6. 读端 API 合并

**职责**：issue 视角的读端 API 合并 issue 域事件 + workflow 域事件，按时间排序返回完整 timeline。

**交互**：
- issue 域事件源：`IssueEvents`（按 issueId 过滤）
- workflow 域事件源：`WorkflowRunEvents`（按 issue.WorkflowRunId 过滤）
- issue 没 workflow run → 只返回 issue 事件
- 排序：`Envelope.Time` 升序

### 7. 删除的 handler

**被删除**：`com.mohist.workflow.run.stopped` / `.failed` 处理器——它的唯一作用是日志 Issue 状态变化。新 issue 事件 `com.mohist.issue.work-aborted` 发出后，EventBridge 的通配 `com.mohist.*` 订阅**自动**转发到 Web UI，handler 价值归零。

## 流程图

### Transition → 发布

```
User / API request
    │
    ▼
Aggregate root holder
    │
    ├── 1. Aggregate transition method
    │       │
    │       ├── 校验不变式
    │       ├── 修改内部 state
    │       └── RecordEvent(IssueEvent.{Variant})
    │
    ├── 2. 持久化 aggregate state (commit)
    │
    ├── 3. 发布 pending events
    │       │
    │       ├── 构造 envelope (CloudEvent 1.0.2)
    │       ├── EventStore.AppendAsync → IssueEvents 表
    │       └── IEventPublisher.PublishAsync → bus
    │               │
    │               ▼
    │          Subscribers (EventBridge → SignalR → Web UI)
    │
    └── 4. 返回结果
```

### Issue events 总线路由

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

### Issue 视角读端 API

```
GET /api/projects/{ref}/issues/{n}/events
    │
    ▼
IssueQuerier.GetInfoAsync → resolve issue + workflowRunId
    │
    ├── EventStore.ListIssueEventsAsync(issueId)
    │       → IssueEvents 表
    │
    ├── EventStore.ListAsync(workflowRunId)
    │       → WorkflowRunEvents 表 (if workflowRunId)
    │
    ▼
Merge + sort by Envelope.Time
    │
    ▼
返回 issue 完整 timeline
```

## 不变式

1. **事件是 past tense 事实**——只能从已成功的 transition 派生，失败抛异常时不发
2. **事件不可变**——构造后不修改，持久化即定论
3. **publish-after-commit**——aggregate state commit 之后再持久化 + 发布事件
4. **publish 失败不丢事实**——事件表已写入即事实存在；bus 通知失败仅记 LogError
5. **事件不携带"应该做什么"**——handler 决策，不在事件里
6. **公共字段一致性**——`IssueId` / `IssueNumber` / `ProjectId` 在每个 envelope 的 extensions 中都存在（便于跨域订阅者路由）

## 与 WorkflowEvent 对称

| 维度 | WorkflowRun | Issue |
|---|---|---|
| 域类型 | `WorkflowEvent` union (17 变体) | `IssueEvent` union (12 变体) |
| 持久化表 | `WorkflowRunEvents` | `IssueEvents` |
| Source prefix | `/mohist/workflow-runs/` | `/mohist/issues/` |
| CloudEvent type | `com.mohist.workflow.*` | `com.mohist.issue.*` |
| Publish 触发 | aggregate state save 之后 | aggregate state save 之后 |
| 读端 | `/api/workflow-runs/{id}/events` | `/api/issues/{id}/events` (合并) |
| EventStore 接口 | 共享 `AppendAsync` / `ListAsync` | 共享 `AppendAsync` + 新增 `ListIssueEventsAsync` |

**完全对称**——同一套基础设施，同一种模式。Issue 域引入不改变 bus / store 的接口约定，仅在 source prefix 路由上做区分。
