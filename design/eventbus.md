---
status: converged
---

# Event Bus

## 定位

领域事件**已经落盘**（各聚合的事件表，业务事实、事件真相）。系统缺的不是 broker / 流式基础设施，而是一个**可靠的通知器**：把已持久化的事件至少一次地推给订阅者。所以不引入 outbox 表、队列、Orleans.Streaming、per-stream consumer grain——**只加一个自驱动的分发器**，且从现在起按分布式设计（多 silo 下分发器必须唯一活跃）。

## 边界：什么进 durable bus

**判定标准**：领域事件 = 领域模型上聚合状态转移所发出、且领域会对其反应的事实。由领域模型定义，**不以消费者判定**。

| 聚合 | 事件 | 进 durable bus |
|------|------|---------------|
| `WorkflowRun`（核心域） | run / stage / task / check 状态转移、Completed/Failed | ✅ |
| `Issue` | work-started / work-completed / closed 等 | ✅ |
| `Runner` | `RunnerDisconnected` 等 | ✅（开放，见末节） |
| `Session` | 无——执行痕迹，横向叶子域，领域不对其反应 | ❌ |

**非领域事件不进 durable bus，各走各路**：

- **AgentSession 生命周期 + transcript**：Session 子域的执行痕迹/遥测，维持 best-effort 痕迹通道。
- **UI 实时推送（`EventBridge`/SignalR）**：presentation，非 domain reaction。摘出 durable bus，走独立 best-effort 实时通道（UI 断线重连后自己对账）。durable bus 内只剩 must-happen、SLA 一致的 domain reaction——这是 SLA 解耦的核心。
- **inbox hint**：通知系统的投影信号，非领域事实。

## 订阅契约

订阅侧机制稳定不变：`ICloudEventHandler` + `[Subscription]` + DI 扫描注册、构造时 freeze。

```csharp
public interface IEventPublisher
{
    Task PublishAsync<TData>(
        TData data, string type, string source, string? subject = null,
        IReadOnlyDictionary<string, string>? extensions = null,
        CancellationToken ct = default) where TData : class;
}

public interface ICloudEventHandler<TData> where TData : class
{
    bool Filter(CloudEvent<TData> evt);
    Task HandleAsync(CloudEvent<TData> evt, CancellationToken ct);
}

public interface ICloudEventHandler   // 动态形态，handler 自己解析 Data
{
    bool Filter(CloudEvent evt);
    Task HandleAsync(CloudEvent evt, CancellationToken ct);
}
```

事件信封是 CloudEvents 1.0.2：统一形态 `CloudEvent`（`Data: JsonElement?`）+ 强类型视图 `CloudEvent<TData>`（bus 在 dispatch 时反序列化构造）。属性清单以代码为准。

**`[Subscription]` 的 `Type` 字符串语法**：

- 字面精确：`com.mohist.workflow.run.completed`
- 段通配：`com.mohist.workflow.*`；全通配：`*`
- `|` 分隔多值：`a|b|c`
- 中间通配符（`foo.*.bar`）禁止

**Handler shape**：

```csharp
[Subscription(Type = "com.mohist.workflow.run.completed")]
public sealed class WorktreeCleanupService : ICloudEventHandler<WorkflowRunCompleted>
{
    public bool Filter(CloudEvent<WorkflowRunCompleted> evt) => true;
    public Task HandleAsync(CloudEvent<WorkflowRunCompleted> evt, CancellationToken ct) { ... }
}
```

**Producer 用法**：metadata 全部是调用点字面量（type 是 reverse-DNS，source 是聚合身份 URI），无中间表：

```csharp
await _publisher.PublishAsync(e,
    type:       "com.mohist.workflow.run.stopped",
    source:     $"/mohist/workflow-runs/{runId}",
    subject:    e.IssueNumber.ToString(),
    extensions: new Dictionary<string, string> { ["projectid"] = e.ProjectId, ["reason"] = e.Reason },
    ct);
```

## 持久性：事件表 + 逐行投递标记

### 存储：不动结构，只加一列

事件真相已按聚合分表落盘（`WorkflowRunEvents` / `IssueEvents` 等：`Id`(per-source 序) · `Source` · `EventId` · `Type` · `Time` · `Subject` · `Data` · `ExtensionsJson`）。**不合并、不新建 outbox 表**，每张事件表只加一列 nullable `DispatchedAt` + 未投递部分索引：

- `DispatchedAt IS NULL` = 未投递；写上时间戳 = 已通知。**这是唯一的投递进度标记**，不另立游标表。
- 事件行不可变（append-only）；`DispatchedAt` 是唯一会被更新的列。
- 毒消息进新表 `DeadLetters`（事件快照 + 失败 handler + error + attempts）。

**为什么不用每流游标**：投递 Seq=N 并前进游标后、handler 完成前崩溃 → 重启看游标已过 N → 不重投 → 退化成 at-most-once。要保住至少一次，游标只能在全部 ack 后前进——那本质就是逐行 `DispatchedAt`。逐行**既最简又正确**。

### 生产侧：事件写入与状态同事务

- 事件行由聚合在状态保存的**同一个 EF 事务**内追加，commit 即持久，崩溃不丢；生产者的 swallow 模式全部消除——写进事务，永不吞。
- `IEventPublisher.PublishAsync` 语义 = **写一行事件**（进环境事务），**不再同步触发任何 handler**——通知是分发器的事。
- **identity 盖印**：发事件时把 `projectid` / `issueid` 盖进 extensions，消除 handler「load run 再读 annotations」的延迟面。

## 事件流（virtual）

**流 = 事件表里 `Source` 相同的行，按 per-source `Id` 有序**。它不是被创建/销毁的实体，是对表的分组视图——像 virtual actor，首条事件 append 进来它就「在」了。

- 流 id = Source（聚合身份，如 `/mohist/workflow-runs/{runId}`、`/mohist/issues/{n}`）；流内序 = per-source `Id`。
- **不做事件溯源**：状态仍单独存，流只负责通知 + 审计。聚合消亡不删行。
- **跨流编排**靠「事件→命令」：`WorkflowRunCompleted` 在 WorkflowRun 流上 → 触发 `CompleteIssue` → Issue 把 `IssueWorkCompleted` 写到自己的流上。每条流自洽。

## 分发模型：单分发器、自唤醒、单查询

```
聚合业务事务 ──写事件行──▶ commit               ← 生产者只追加，不通知任何人
        │
  分发器（集群单例，唯一通知者）
  ┌──────────────────────────────────────┐
  │ Orleans reminder ~1s 自唤醒           │ ← 唯一驱动源，持久化，自愈
  │   单查询拉各表未投递事件               │   （UNION、ORDER BY Source,Id、LIMIT 封顶）
  │   逐条: 按 [Subscription] 扇出         │ → ICloudEventHandler（机制不变）
  │         Polly 重试; 耗尽 → DeadLetters │
  │   逐条: UPDATE DispatchedAt = now     │
  └──────────────────────────────────────┘
```

- **生产者只追加，不唤醒**（Kafka 心智：broker=DB log，consumer 自己 poll）。分发器 = Orleans 命名 grain（key `"dispatcher"`）+ 持久化 reminder ~1s 自唤醒：silo 崩 → 别处重激活 → 自愈。无外部 lease / 选主。**正确性不依赖任何外部信号**——哪怕所有 ping 都丢，下一 tick 照样捞到。
- **延迟 ≤ 一个 tick（~1s）**。若嫌慢，生产者 commit 后可 best-effort `Pulse()` 立即触发——纯延迟优化，正确性不依赖它。
- **单查询捞所有流**：成本随未投递事件数线性（LIMIT 封顶），与流总数无关。**流的数量 ≠ 拉取者数量**——拉取者数 = 分发器数（现在 1），这正是砍掉 per-stream grain 的理由。
- **per-stream FIFO**：单分发器串行、按 `(Source, Id)` 严格按序、逐条标记 → 同流不乱序、不跳号。公平性上 `ORDER BY Source, Id` 会先抽干一条流；个人级事件量无饥饿，将来若有可改每流轮转（本期不做）。
- **崩溃恢复 / at-least-once**：投递后、标记前崩溃 → 该行仍 NULL → 重投 → **handler 按 `EventId` 幂等吸收**（handler 必须幂等）。Polly 耗尽 → 进 `DeadLetters` 并置位 `DispatchedAt`，停止重试（防毒消息循环），可查询、可手动重投。

### 分布式形态与并行口子

- 多 silo + 共享 DB（事件表跨 silo 可见）→ Postgres；本地单机单 silo 仍可 SQLite。
- 未来要并行是纯加法：N 个分发器 grain，key = `hash(Source) % N`，同一 Source 永远落同一 grain → FIFO 不破。本期不做。

### Subscription 是扇出目标，不是拉取者

Subscription = type 过滤 + handler，不持有 offset。分发器对每个事件按 type 扇出给所有匹配 subscription——Mohist 的 domain reaction 是「共发反应」，不是各自独立消费同一份 feed。**无 per-subscription offset**：per-handler 重试是投递内瞬态（Polly），某个耗尽进它自己的 DLQ，置位 `DispatchedAt` 后 per-handler 结果丢弃。

## 错误处理阶梯

- **消除**：事件写入与状态同事务 → 消灭 commit/publish 间隙。
- **掩盖**：Polly 指数退避吸收瞬态失败。
- **聚合**：唯一顶层错误处理器在分发器的扇出 service；handler 不再各自吞异常。
- **可见**：毒消息进 `DeadLetters`，可查询、可重投。

## 开放问题

1. **(开放) `Runner` 事件归类**：`RunnerDisconnected` 按标准是领域事件，但 Runner 偏执行资源侧——待确认。
2. **(开放) best-effort 实时通道机制**：`EventBridge` 摘出后 UI 推送走什么（保留 in-memory SignalR fanout 还是另起轻量通道）。
3. **(开放) `EpicReconciliationService` 去留**：durable 投递落地后纯补漏扫描可移除；若仍承担 epic 聚合职责则保留。
4. **(开放) `[Reentrant]` 移除**：分发离栈后不再依赖它；移除有正确性风险，单独评估（另见 architecture.md 持久化原则）。
5. **(开放) DLQ 是否首期必须**：无 DLQ 毒消息会每 tick 重试刷日志，建议首期就带轻量 DLQ。

## 差距脚注

正文是目标态，当前运行时差距（落地跟踪 epic #36）：

- 运行时仍是 `InMemoryEventBus`：分发在发布者调用栈上同步执行、异常静默吞、无重试无 DLQ，正确性靠对账扫描兜底。
- `DispatchedAt` 列已上线（且实际覆盖了 WorkflowRun / Issue / Epic / AgentSession 四张事件表——比本文"Session 不进 durable bus"的边界宽，归类待与开放问题 1 一并裁决）；分发器、同事务写入、EventBridge 摘出未交付。
- `TimeProvider` 注入、`EventStore` 序列号改自增、未用的 CloudNative.CloudEvents 依赖清理，随 epic #36 顺带收敛。

## Related

- [`architecture.md`](architecture.md) — 执行事实与状态裁判分离、两层事件通道 SLA。
- [`domain-analysis.md`](domain-analysis.md) / [`workflow/issue-coordination.md`](workflow/issue-coordination.md) — 聚合与领域事件来源。
- 借鉴 CloudEventDotNet（借设计不引依赖）：Polly resilience pipeline 挂载点、DLQ DTO + `dl:` 前缀防递归。不借 core / 传输 / per-stream consumer / 双循环 poller。
