---
purpose: "事件总线目标态：把已落盘的领域事件可靠地（持久化 at-least-once）通知给订阅者。极简路线——不引入 broker / 流式 SDK / per-stream grain，只加一个自驱动的分发器。eventbus.md 是 as-is 参考；本文是 target。"
style:
  - "只记录已收敛的决策与理由；开放问题单列，不作决策。"
  - "中文为主，表格 + 少量代码/ASCII。"
include:
  - "领域事件范围与判定标准。"
  - "持久性：复用现有两张事件表 + 逐行 DispatchedAt。"
  - "分发模型：单分发器、自唤醒、单查询、反射扇出、逐行标记。"
  - "事件流（virtual，= Source 分组 + per-source Id 序）。"
  - "分布式形态与并行扩展口子。"
exclude:
  - "实现细节（grain 内部循环、EF mapping、SQL 细节）。"
  - "各 handler 的具体反应逻辑；见 design/workflow/issue-coordination.md。"
status: "目标态，未交付。设计已收敛，运行时仍是 InMemoryEventBus（best-effort，静默吞异常）。落地跟踪：epic #36。带「(开放)」的条目未定稿。"
---

# Event Bus v2（目标态）

> 与 [`eventbus.md`](eventbus.md) 的关系：后者记录当前 in-memory best-effort 总线（含「持久化: 无」），是 as-is 参考。本文是 target 设计。

## 动机

当前总线（`InMemoryEventBus`）best-effort：分发在发布者调用栈上同步执行，handler / publish 异常**静默吞掉**，无重试、无 DLQ。正确性长期靠**对账扫描**兜底（`IssueWorkflowReconciliationService` 24h 等）。

issue #307 移除 issue 侧兜底扫描、改事件驱动流转后，这条「静默丢 + 扫描补」的链路被暴露：分发瞬间失败 → issue 卡死、无自动恢复。

**奥卡姆剃刀后的判断**：领域事件**已经落盘**（`WorkflowRunEvents` / `IssueEvents`，业务事实、事件真相）。我们缺的不是一套 broker / 流式基础设施，而是一个**可靠的通知器**——把这些已经真实发生并持久化的事件，至少一次地推给订阅者。所以：不引入独立 outbox 表、不引入队列、不引入 Orleans.Streaming、不引入 per-stream consumer grain。**只加一个自驱动的分发器。**

**从现在起按分布式设计**：分发器在多 silo 集群下必须唯一活跃。

## 领域事件：范围与判定（已收敛）

**判定标准**：领域事件 = **领域模型上聚合状态转移所发出、且领域会对其反应的事实**。由领域模型定义，**与谁消费、要不要持久化无关**（不以消费者判定）。

据此枚举（依据 [`domain-analysis.md`](domain-analysis.md):57-61,85 与 [`workflow/issue-coordination.md`](workflow/issue-coordination.md):23-27）：

| 聚合 | 领域模型上的事件 | 进 durable bus |
|---|---|---|
| `WorkflowRun`（核心域） | run / stage / task / check 状态转移、`WorkflowRunCompleted`/`Failed`（→ CompleteIssue / AbortWork） | ✅ |
| `Issue` | work-started / work-completed / closed 等 | ✅ |
| `Runner` | `RunnerDisconnected` 等（→ Session 自决失败） | ✅（开放：见下） |
| `Session` | 无（执行痕迹，横向叶子域，领域不对其反应） | ❌ |

**非领域事件，不进 durable bus**，走各自通道：
- **AgentSession 生命周期 + transcript**：Session 子域（独立子域，见 [`domain-analysis.md`](domain-analysis.md)）的执行痕迹/遥测。`Session` 是横向叶子域——被多上下文消费、不反向依赖任何业务上下文。维持 best-effort 痕迹通道。
- **UI 实时推送（`EventBridge`/SignalR）**：presentation，非 domain reaction。**摘出 durable bus**，走独立 best-effort 实时通道（UI 断线重连后自己对账）。理由见「stream 与 subscription」的 SLA 解耦。
- **inbox hint**（`com.mohist.inbox.item-persisted`）：通知系统的投影信号，非领域事实。

**边界 sharpen**：当前 in-memory bus 把「领域事件 + 执行痕迹 + UI 推送 + 通知投影」混在一条 `com.mohist.*` 上。本次重构把 durable bus 收敛为**纯 domain reaction**（必须发生、SLA 一致），其余各走各路，呼应 [`architecture.md`](architecture.md) 的「执行事实与状态裁判分离」。

## 持久性：复用现有事件表 + 逐行投递标记（已收敛）

### 存储：不动结构，只加一列

事件真相已在两张表里，**不合并、不新建 outbox 表**：

| 表 | 角色 | 现有列 | 新增 |
|---|---|---|---|
| `WorkflowRunEvents` | workflow 事件真相 | `Id`(per-source 序) · `Source` · `EventId` · `Type` · `Time` · `Subject` · `Data` · `ExtensionsJson` | **`DispatchedAt`**(nullable) |
| `IssueEvents` | issue 事件真相 | 同上 | **`DispatchedAt`**(nullable) |
| `DeadLetters` | DLQ（毒消息隔离） | — | 新表（毒消息快照 + 失败 handler + error + attempts） |

- `DispatchedAt IS NULL` = 未投递；写上时间戳 = 已通知。**这是唯一的投递进度标记**——不另立 `DeliveryOffsets` 游标表。
- 索引：每张事件表加 `WHERE DispatchedAt IS NULL` 的部分索引（或 `(DispatchedAt, Source, Id)`）供分发器快查。
- 事件行**不可变**（append-only）；`DispatchedAt` 是唯一会被更新的列。

### 生产侧：事件写入必须与状态同事务（目标）

- 现状 `EventStore.AppendAsync` 用**独立 DbContext** 写事件行（与状态保存不在同一事务）——崩溃间隙会丢事件。目标：`WorkflowRunStore.SaveAsync` / `IssueGrain.SaveIssueAsync` 把事件行写进**同一个 EF 事务**。commit 即持久，崩溃不丢。
- 三处生产者当前三种 swallow 模式收敛为一种：**写进行务，永不吞**。
- `IEventPublisher.PublishAsync` 语义收敛为「写一行事件」（进环境事务）；**不再同步触发任何 handler**——通知是分发器的事。
- **identity 盖印**：发事件时把 `projectid`/`issueid` 盖进 extensions（消除 handler「load run 再读 annotations」的延迟面）。

### 为什么不用每流游标（DeliveryOffsets）

游标破坏 at-least-once：投递 Seq=N 并前进游标后、handler 完成前崩溃 → 重启看游标已过 N → 不重投 → 退化成 at-most-once。要保持至少一次，游标只能在 handler 全部 ack 后前进并持久化——那本质又退回「逐行 DispatchedAt」。故 **`DispatchedAt`（逐行）既最简又正确**。

## 事件流（virtual）（已收敛）

**流 = 一张事件表里 `Source` 相同的行，按 per-source `Id` 有序。** 它不是被创建/销毁的实体，是对表的分组视图——`WHERE Source=@s ORDER BY Id`。像 virtual actor：无需 `Open/Close/Exists`；首条事件 append 进来它就「在」了。

- **流 id = Source**（= 聚合身份，如 `/mohist/workflow/001`、`/mohist/issues/42`）。
- **流内序 = per-source `Id`**（已有，单调递增）。
- **不做事件溯源**：状态仍单独存，流只负责通知 + 审计。
- **无 registry、无生命周期**：Issue Close/Reopen、Runner Register/Unregister 只是往同一条流继续 append。聚合消亡不删行（审计留存）。
- **跨流编排**：`WorkflowRunCompleted` 在 WorkflowRun 流上 → 触发 `CompleteIssue` → Issue 把 `IssueWorkCompleted` 写到 Issue 自己的流上。每条流自洽，跨聚合靠「事件→命令」。

> 与早期方案的差异：早期把流做成「per-stream 自拉 + virtual worker grain」。现已砍掉——**流只是日志视图，不是运行时实体，没有自己的拉取者/激活**。运行时只有一个分发器（见下）。

## 分发模型：单分发器、自唤醒、单查询（已收敛）

### 一个组件，一个循环

```
聚合业务事务 ──写事件行(已有)──▶ commit              ← 生产者只追加,不通知任何人
        │
  分发器(集群单例, 唯一通知者)
  ┌──────────────────────────────────────┐
  │ Orleans reminder ~1s 自唤醒           │ ← 唯一驱动源,持久化,自愈
  │   单查询拉两表未投递事件:             │
  │     WHERE DispatchedAt IS NULL       │
  │     ORDER BY Source, Id LIMIT N      │
  │   逐条: 反射 [Subscription] 扇出      │ → ICloudEventHandler(已有机制)
  │         Polly 重试; 耗尽 → DeadLetters│
  │   逐条: UPDATE DispatchedAt = now    │
  └──────────────────────────────────────┘
```

**就这一个组件**。订阅者侧机制（`ICloudEventHandler` + `[Subscription]` + 反射扫描）原样复用。

### 谁驱动 / 谁唤醒

- **生产者只追加，不唤醒。** 通知完全是分发器的职责（Kafka 心智：broker=DB log，consumer 自己 poll，broker 不 push）。
- **分发器自唤醒**：一个集群单例（Orleans 命名 grain `"dispatcher"` + **reminder** ~1s）。reminder 持久化：silo 崩 → 在别 silo 重激活 → 循环自愈。**无需外部 lease / 选主 / 重平衡。**
- **正确性不依赖任何外部信号**：哪怕所有「ping」都丢，下一 tick 照样捞到。这是它比信号驱动可靠的根本原因。
- **延迟**：≤ 一个 tick（~1s）。24h → ~1s。个人级够用；若某天嫌慢，生产者 commit 后可 best-effort `dispatcher.Pulse()` 立即触发一次——**正确性不依赖它**，纯延迟优化。

### 单查询捞所有流（不是每流一个拉取者）

100 万个 workflow = 100 万组事件**行**（数据量，不可避免），但**只有 1 个拉取者**：

```sql
-- 分发器每 tick 一次,无论多少条流
SELECT * FROM (
    SELECT 'workflow' AS Agg, Source, Id, EventId, Type, Subject, Data, ExtensionsJson, ...
    FROM WorkflowRunEvents WHERE DispatchedAt IS NULL
    UNION ALL
    SELECT 'issue' AS Agg, Source, Id, EventId, Type, Subject, Data, ExtensionsJson, ...
    FROM IssueEvents WHERE DispatchedAt IS NULL
) t ORDER BY Source, Id LIMIT 100;
```

成本随**未投递事件数**线性（被 LIMIT 封顶），与**流总数无关**。没新事件时走索引返回空，近乎零成本。

> **流的数量 ≠ 拉取者数量。** 拉取者数 = 分发器数（现在 1，将来分片 N）。这正是砍掉 per-stream grain 的核心理由。

### per-stream FIFO 怎么保证

单分发器串行、按 `(Source, Id)` 处理 → 同一 Source 的事件必然按 per-source `Id` 依次投递 → **每流 FIFO**。又因**逐条标记 `DispatchedAt`、且严格按序**：永不会在 Id=5 未标记前投递 Id=9 → **不乱序、不跳号**。

> 公平性：`ORDER BY Source, Id` 会先抽干一条流再下一条。个人级事件量下无饥饿；若将来某条活跃流挤压其他流，可改为「每 tick 每流最老一条轮转」——本期不做。

### 崩溃恢复 / at-least-once

- 分发器投递某事件后、`UPDATE DispatchedAt` 前崩溃 → 该行仍 `NULL` → 重启重投 → **handler 按 `EventId` 幂等吸收**（`IssueGrain.CompleteWorkAsync` 先查状态，天然幂等）。
- handler 持续失败、Polly 耗尽 → 进 `DeadLetters`，`DispatchedAt` 置位，**停止重试**（防毒消息无限循环）；可查询、可手动重投。

## 分布式形态

- 多 silo 集群 + **共享 DB**（两事件表 + DeadLetters 跨 silo 可见）→ **Postgres**（SQLite 不支持多进程并发写）。本地单机单 silo 仍可用 SQLite。
- 分发器 = Orleans 集群单例 grain（命名 key `"dispatcher"`）：Orleans placement 保证全集群**唯一 activation** → 唯一通知者，无 claim 竞争、无 leader 选举。
- reminder 持久化于 membership 表，silo 故障自愈。

### 并行扩展口子（本期不做）

单分发器串行，个人级吞吐足够。**未来要并行**时是纯加法，模型不变：

- 分 N 个分发器 grain，key = `hash(Source) % N`；每个只拉自己那批 Source（`WHERE DispatchedAt IS NULL AND hash(Source)%N = k`）。
- 同一 Source 永远落同一 grain → 单 owner → **per-stream FIFO 不破**。
- 拉取者数 = N（常数），**永远 ≠ 流数**。

## stream 与 subscription：两层（已收敛）

- **Stream** = 日志 + 逐行 `DispatchedAt`（per-aggregate）。拉取与订阅无关。
- **Subscription** = `type 过滤 + handler`，是**投递落点（扇出目标）**，不是拉取者，不持有任何 offset。

分发器对每个事件**按 type 扇出给所有匹配 subscription**。**无 per-subscription offset**——Mohist 的 domain reaction 是「共发反应」（`WorkflowRunCompleted` 发生时各 reaction 一起发生），不是各自独立消费同一份 feed。per-handler 重试是投递内瞬态（Polly）；某个耗尽进它自己的 DLQ；置位 `DispatchedAt` 后 per-handler 结果丢弃。

**SLA 解耦 = 摘出 best-effort 观察者**：把 `EventBridge`（UI 推送）**摘出 durable bus**，走独立 best-effort 实时通道（UI 重连对账）。durable bus 内只剩 must-happen、SLA 一致的 domain reaction。

## 模块设计（深模块 / 信息隐藏）

### 生产侧：写事件（进环境事务）

`IEventStore` 扩一个事务内写入入口（或 store 直接写 `WorkflowRunEvents`/`IssueEvents` 行）；`AppendAsync` 现语义收敛为「写一行」。`IEventPublisher.PublishAsync` 不再触发 handler。

### 消费侧：单分发器 grain + 扇出 service

```csharp
public interface IDispatcherGrain   // Orleans grain, 集群单例 key="dispatcher"; IRemindable 自唤醒
{
    Task PulseAsync(CancellationToken ct = default);  // 一次拉取-扇出-标记循环
}

// 分发核心逻辑抽成纯 DI service, 可单测:
//   拉取查询、按 type 路由、调 handler、Polly 重试、DLQ、标记 DispatchedAt
// 用 fake IEventStore/IDeadLetters + 注入 TimeProvider 单测; IDispatcherGrain 只是薄壳。
```

### 消费者契约：不变

`ICloudEventHandler<TData>` + `[Subscription]` + 反射扫描**全部保留**。各 handler 内部吞异常的 try-catch 移除，交给分发器聚合。**handler 必须幂等**（at-least-once）。

## 错误处理阶梯（已收敛）

当前是反模式最差档「到处吞、静默丢」。升级：
- **消除**：事件写入与状态同事务 → 消灭 commit/publish 间隙。
- **掩盖**：Polly 指数退避吸收瞬态失败。
- **聚合**：唯一顶层错误处理器在分发器的扇出 service；handler 不再各自吞。
- **可见**：毒消息进 `DeadLetters`，可查询、可重投。

## 借鉴 CloudEventDotNet：借设计，不引依赖（已收敛）

| 借鉴 | 落点 |
|---|---|
| Polly resilience pipeline 挂载点 | per-handler 重试/退避 |
| DLQ DTO + `dl:` 前缀防递归 | `DeadLetters` 毒消息处理 |

**不借**：core（与现有抽象重叠）、Kafka/Redis 传输（broker 已是 DB log）、per-stream consumer（分发器单实例取代）、双循环 poller（无 reclaim 需求——逐行 DispatchedAt 即进度）。

## 顺带修复（已纳入）

- **`TimeProvider` 注入**：替换 bus/store/dispatcher 里的 `DateTimeOffset.UtcNow`（违反测试铁律）。`TimeProvider.System` 已注册。
- **序列号竞态**：`EventStore` 的 `MAX(Id)+1` 换 DB 自增更稳。注：Orleans 单写者 grain 已使同 Source 串行，竞态理论不发生；自增只是消除残余风险。
- **CloudNative.CloudEvents 死重**：csproj 引用但未用，可清理。

## 开放问题（未定稿）

1. **(开放) `Runner` 事件归类**：`RunnerDisconnected` 等是否进 durable bus？按标准应为领域事件，但 Runner 偏执行资源侧——待确认。
2. **(开放) best-effort 实时通道机制**：`EventBridge` 摘出后，UI 实时推送走什么（保留现 in-memory SignalR fanout？另起轻量通道？）——待定。
3. **(开放) `EpicReconciliationService` 去留**：durable 投递落地后，纯补漏扫描可移除；若它仍承担 epic 聚合（done = 所有 issue done）职责则保留。
4. **(开放) `[Reentrant]` 移除**：分发离栈后不再依赖它；移除有正确性风险，单独评估。
5. **(开放) DLQ 是否首期必须**：极简可先只做 at-least-once 重试（毒消息持续重投），DLQ 作为二期。但无 DLQ 毒消息会每 tick 重试、刷日志——建议首期就带轻量 DLQ。

## 落地顺序（建议拆 issue）

1. 两张事件表加 `DispatchedAt` 列 + 部分索引；建 `DeadLetters` 表；`IEventStore` 加投递标记/读取未投递入口。
2. 三处 store 改事务内写事件行 + identity 盖印；`IEventPublisher` 收敛为「写一行」，不再触发 handler。
3. `IDispatcherGrain`（集群单例 + reminder）+ 扇出 service（拉取 UNION 查询、按 type 路由、Polly、DLQ、标记 `DispatchedAt`）+ `TimeProvider`。
4. 摘出 `EventBridge` 到 best-effort 通道。
5. 收敛 handler（去吞异常 try-catch、落实幂等）。
6. 清理（评估移除扫描 / `[Reentrant]`，更新测试 fake 接缝）。

## Related

- [`eventbus.md`](eventbus.md) — as-is 总线参考。
- [`architecture.md`](architecture.md) — 执行事实与状态裁判分离。
- [`domain-analysis.md`](domain-analysis.md) / [`workflow/issue-coordination.md`](workflow/issue-coordination.md) — 聚合与领域事件来源。
- [`testing.md`](testing.md) — `TimeProvider` 注入、fake 接缝约束。
- 参考（外部，已克隆至 `/home/szf/repos/CloudEventDotNet`）：Polly pipeline、`PubSubDeadLetterSender.cs`。
