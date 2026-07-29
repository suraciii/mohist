---
status: converged
---

# Event Bus

## 做什么

领域事件已经持久化（各聚合自带事件表）。系统需要的是一个通知器：把已持久化的事件以 at-least-once 语义投递给订阅者。不要 broker、不要队列、不要 streaming SDK、不要 per-stream grain，只要一个自驱动的分发器。

## 什么进总线

分发器一个周期内只查五张事件表的未分发行；这五个聚合是该总线的耐久来源。

| 聚合 | 事件 | 进总线？ |
|---|---|---|
| WorkflowRun | 状态迁移、Completed、Failed | 是 |
| Issue | work-started、work-completed、closed | 是 |
| Epic | 状态迁移、状态自动唤醒 | 是 |
| AgentSession | runtime bound、状态变化 | 是 |
| AgentJob | Failed | 是 |

这五个来源覆盖当前所有通过事件总线推进的跨聚合流程；其他领域（Runner、Project 等）的副作用要么不产生可分发的领域事件，要么由上述聚合的事件携带必要上下文并代为分发。Session 是叶子级追踪域，没有任何域对它作出反应——`AgentSession` 行虽进总线，但仅承担 dispatch 角色，不代表它被其他域订阅消费。

## 订阅契约

`ICloudEventHandler` + `[Subscription]` + DI。机制稳定，不变。

两类消费者匹配同一信封，匹配机制按消费面分工：

- **系统消费者**（`[Subscription]` handler）用编译期注册的 type glob——这是
  表达式能力的子集（等价于 `event.type == ...` 与前缀匹配）。系统 handler
  是代码不是配置，不需要运行期表达式。
- **用户消费者**（Agent 路由表）用 [`event-protocol.md`](event-protocol.md)
  定义的 CEL 子集 matcher，匹配整个事件信封（`type`、`source` 与全部
  context 扩展属性同权）。

对称性要求不变：系统 handler 能路由到的事件，用户表达式同样订得到。

`[Subscription]` 的 type 匹配规则：

| 模式 | 匹配 |
|---|---|
| `com.mohist.workflow.run.completed` | 精确 |
| `com.mohist.workflow.*` | 前缀通配 |
| `*` | 全部 |
| `a\|b\|c` | 任一 |
| `foo.*.bar` | 禁止 |

## 持久化

每张事件表加一列：`DispatchedAt`（可空）。这是唯一的投递标记。

- `NULL` = 未投递。
- `timestamp` = 已投递。
- 没有游标表，没有 per-stream offset。

事件与状态保存写在同一个 EF 事务里。提交即持久化。
`PublishAsync` 只写一行，绝不触发 handler。通知是分发器的事。

## Stream

一个 stream = 事件表中 `Source` 相同的所有行，按 per-source `Id` 排序。

- Stream id = Source（如 `/mohist/workflow-runs/{runId}`）。
- 不是 event-sourced。状态另行存储。Stream = 通知 + 审计。
- 跨 stream：事件 → 命令。`WorkflowRunCompleted` → `CompleteIssue`。

## 分发器

```
Transaction ──写入行──▶ commit           ← 生产者追加并唤醒
        │                          ▲
   Dispatcher（集群单例）
   ┌──────────────────────────────────────────────┐
   │ Orleans reminder（ReminderPeriod tick）      │
   │   查询未分发的行（按 Source, Id 排序）        │
   │   逐行：fanout 到各 handler                   │
   │   手写指数退避（EventDispatcherOptions）      │
   │   超额 → DLQ；落地后标记 DispatchedAt         │
   └──────────────────────────────────────────────┘
```

### 唤醒

**每个耐久生产者**（WorkflowRun、Issue、Epic、AgentSession、AgentJob）在它的事件写入事务提交或失败事件追加成功后，立刻通过 `EventDispatcherPoke.PokeAfterCommit` 向 `IEventDispatcherGrain` 发起一次 fire-and-forget 唤醒。poker 失败（dispatcher grain 不可用、序列化失败等）被 helper 吞掉并记 debug 日志，生产者的命令结果与持久化行不受影响；没有事件产生的命令（如幂等 Epic 操作）不触发唤醒。

**reminder 才是正确性路径**。唤醒只是把下一个分发周期从「最长 `ReminderPeriod` 后」提前到「下一次分发表调度器让出时」。一旦唤醒丢失、被吞、进程无 dispatcher grain、reminder 比事件先 tick，都不会让事件丢失：reminder 仍然会查到未分发行并按 FIFO 投递。

### 退避

**手写指数退避**，不接受任何第三方重试 / 弹性库。每条事件匹配到的 handler 各自维护 attempt 计数和下一次可重试时间，下一次可重试时间由 `EventDispatcherOptions.BaseBackoff` × 2^(attempt-1) 计算，上限 `EventDispatcherOptions.MaxBackoff`。handler 累计到 `EventDispatcherOptions.MaxAttempts` 后停止重试，分发器把死信写入 `IDeadLetterStore` 并把事件标为已分发；handler 计数与下一次可重试时间不持久化。

一个 handler 的失败不影响其他 handler 的 attempt 计数；同一行的不同 handler 独立推进。

### 结算保留

分发器在每个 event key 上维护一张「已结算 handler 状态」的进程内表（`Completed` 或 `DeadLettered`）。只有当 `IEventStore.MarkDispatchedAsync` 或 `IDeadLetterStore.SettleAsync` 真正落地后，才把这一行的事件键从状态表里删除；结算写失败时状态保留，下一个周期只重试结算写，不重跑 handler、不重置 attempt 计数。

这是一条**进程内**保留策略：分发器进程重启会把状态表清空，reminder 把未分发行重新喂回，handler attempt 从 0 重新开始。retry 状态不持久化是当前约束，不在本期范围内承诺。

### FIFO 阻塞与可见性

每个分发周期内按 `(Source, Id)` 顺序投递单 source 的行。同一 source 中如果存在一条 handler 还在等待下一次重试的行，则这条 source 的后续行在本周期跳过（避免打破 FIFO），其他 source 不受影响；它们各自独立推进。

每个周期结束后，分发器把这一周期被阻塞的 source 数量写进一个单元为 `1`、不带任何属性的 ObservableGauge：`mohist.server.event_dispatcher.blocked_sources`。source 标识符、event id、attempt 等**永不作为 metric attribute**——避免高基数。Gauge 报告的是上一次完成的分发周期数；周期进行中或 dispatcher 刚启动时该值为 0。这是当前给运维侧的唯一阻塞可见信号。

### 崩溃与恢复

- 任意周期中间崩溃：未标 `DispatchedAt` 的行仍在，reminder 重投；handler 必须按 `EventId` 幂等。
- DLQ 行：进 DLQ 时立即标 `DispatchedAt`，可查询、可由 `IDeadLetterStore` 提供的 `StartRedeliveryAsync` / `ResolveAsync` / `RecordRedeliveryFailureAsync` 流程手动重投，HTTP 接口契约不变。本期不引入新的 DLQ 查询 / 重投 API。

## 错误阶梯

- 消除：事件与状态同事务。
- 吸收：手写指数退避（`EventDispatcherOptions.BaseBackoff` / `MaxBackoff` / `MaxAttempts`）。
- 聚合：分发器兜底捕获，handler 绝不吞异常。
- 暴露：DLQ，可查询、可重试。
