---
status: converged
---

# Event Bus

## 做什么

领域事件已经持久化（各聚合自带事件表）。系统需要的是一个通知器：把已持久化的事件以 at-least-once 语义投递给订阅者。不要 broker、不要队列、不要 streaming SDK、不要 per-stream grain，只要一个自驱动的分发器。

## 什么进总线

| 聚合 | 事件 | 进总线？ |
|---|---|---|
| WorkflowRun | 状态迁移、Completed、Failed | 是 |
| Issue | work-started、work-completed、closed | 是 |
| Runner | Disconnected（开放） | 是 |
| Session | — | 否 |

Session 是叶子级追踪域，没有任何域对它作出反应。

## 订阅契约

`ICloudEventHandler` + `[Subscription]` + DI。机制稳定，不变。

订阅的匹配语言是 [`event-protocol.md`](event-protocol.md) 定义的 CEL 子集 matcher：一条布尔表达式匹配整个事件信封（`type`、`source` 与全部 context 扩展属性同权）。系统消费者（`[Subscription]` handler）与用户消费者（Agent 路由表）共用同一 matcher 语义，收敛随统一事件路由 epic 推进。

### 实装差距

当前 `[Subscription]` 仍是对 type 的 glob 匹配，表达式 matcher 未实装：

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
Transaction ──写入行──▶ commit           ← 生产者只追加
        │
   Dispatcher（集群单例）
   ┌──────────────────────────────┐
   │ Orleans reminder ~1s tick     │
   │   查询未分发的行              │
   │   逐行：fanout 到各 handler   │
   │   Polly 重试；死信 → DLQ      │
   │   UPDATE DispatchedAt = now   │
   └──────────────────────────────┘
```

- 生产者只追加，绝不唤醒分发器。
- 分发器 = Orleans named grain + 持久化 reminder，自愈。
- 每个 tick 一次查询。成本 = 未分发行数，而非 stream 数。
- Per-stream FIFO：串行分发，按 (Source, Id) 排序。
- 崩溃：行仍为 NULL → 重投。handler 必须按 EventId 幂等。
- 毒消息：进 DLQ 并置 DispatchedAt。可查询、可手动重试。

未来：N 个分发器按 `hash(Source) % N` 分片，同一 source → 同一 grain。暂不做。

## 错误阶梯

- 消除：事件与状态同事务。
- 吸收：Polly 指数退避。
- 聚合：分发器兜底捕获，handler 绝不吞异常。
- 暴露：DLQ，可查询、可重试。
