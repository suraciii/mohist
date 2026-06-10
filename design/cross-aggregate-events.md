---
purpose: "How domain events flow between aggregates. Each aggregate's event catalogue lives in its domain model; this document captures what code cannot say — the boundaries, the triggers, the reactions, and the responsibilities that span aggregates."
include:
  - "Cross-aggregate event sources and consumers."
  - "Synchronous call paths that bypass the event bus."
  - "Why a reaction lives in the consumer, not the producer."
  - "Failure modes that the event infrastructure tolerates."
exclude:
  - "Specific event variants; see the domain model files (e.g. WorkflowEvent.cs, IssueEvent.cs)."
  - "Bus, envelope, and store plumbing; see eventbus.md."
  - "EF migrations, HTTP routes, file paths."
---

# Cross-Aggregate Events

## 背景

Mohist 的核心交互是 **issue ↔ workflow run**。Issue 启动 workflow，workflow run 推进，workflow 终结，issue 跟着收尾。两个聚合根都把状态变化作为不可变事实记录——`WorkflowEvent` 由 `WorkflowRun` method 返回，`IssueEvent` 由 `Issue` transition 记录。

聚合根各自在 domain model 里描述自己的事件集合、状态机、不变式。**本文不重复事件清单**——它聚焦**跨聚合的契约**：

- 谁产生什么事件？
- 谁消费、谁不消费？
- 什么时候走事件总线，什么时候走同步调用？
- 反应的责任在哪一方？

## 聚合之间的两条交互路径

### 路径 A：同步调用

聚合根持有者直接调另一个聚合的方法。**快、强一致、显式**。

```
Issue start        → WorkflowRun.StartAsync
Workflow complete  → Issue.CompleteWorkAsync
Workflow fail      → Issue.AbortWorkAsync
User cancel        → WorkflowRun.StopAsync  +  Issue.Close
```

调用方知道自己在做什么。失败抛异常，调用方决定是否 retry 或回退。

### 路径 B：事件总线

聚合根状态变化后通过事件总线广播。**其他上下文按需订阅**。**响应方异步、幂等、自决**。

```
WorkflowRun.completed     →  EventBridge → SignalR → Web UI
Issue.archived            →  EventBridge → SignalR → Web UI
Runner.disconnected       →  AgentSession.fail active sessions
```

订阅方**自己决定**怎么响应。事件不携带"应该做什么"——携带"发生了什么"。

## 触发矩阵

下表列出每个领域事件的可能响应方。**空白格表示"无跨聚合响应"**——事件只作审计/UI 推送。

### WorkflowRun 事件

| 事件 | Issue 响应 | Runner / Session 响应 | UI 响应 |
|---|---|---|---|
| `WorkflowRunStarted` | 同步：已通过 `Issue.StartWorkflow` 在调 `WorkflowRun.StartAsync` 之前发生；无异步响应 | — | 推送 |
| `WorkflowRunResumed` | — | — | 推送 |
| `WorkflowRunPaused` | — | — | 推送 |
| `WorkflowRunStopped` | 同步：`IssueGrain` 调 `Issue.AbortWorkflow` 走同步路径（不变式匹配） | — | 推送 |
| `WorkflowRunCompleted` | 同步：`IssueGrain` 调 `Issue.CompleteWork` 走同步路径 | — | 推送 |
| `WorkflowRunFailed` | 同步：`IssueGrain` 调 `Issue.AbortWorkflow` | — | 推送 |
| `StageStarted` | — | — | 推送 |
| `StageCompleted` | — | — | 推送 |
| `StageFailed` | — | — | 推送 |
| `StageApprovalRequested` | — | — | 推送（UI 弹审批对话框） |
| `StageApprovalResolved` | — | — | 推送 |
| `TaskCompleted` / `TaskFailed` | — | — | 推送 |
| `CheckPassed` / `CheckFailed` / `CheckPending` | — | — | 推送 |
| `RepairScheduled` | — | — | 推送 |

### Issue 事件

| 事件 | Workflow 响应 | Runner / Session 响应 | UI 响应 |
|---|---|---|---|
| `IssueCreated` | — | — | 推送（Backlog 列表） |
| `IssueLabelsChanged` | — | — | 推送（kanban 分类） |
| `IssuePriorityChanged` | — | — | 推送（Backlog 排序） |
| `IssuePrerequisiteAdded` | — | — | 推送（依赖图） |
| `IssuePrerequisiteRemoved` | — | — | 推送 |
| `IssueWorkStarted` | 同步：调 `WorkflowRun.StartAsync` 在此 transition 之前完成；无异步响应 | — | 推送 |
| `IssueWorkCompleted` | — | — | 推送（Done 状态） |
| `IssueWorkAborted` | — | — | 推送（失败原因） |
| `IssueClosed` | 同步：调 `WorkflowRun.StopAsync` 在此 transition 之前完成 | — | 推送 |
| `IssueArchived` | — | **worktree 清理由 archive 路径显式负责**（非事件订阅） | 推送 |
| `IssueUnarchived` | — | — | 推送 |
| `IssueReopened` | — | — | 推送 |

### Runner 事件

| 事件 | Issue 响应 | AgentSession 响应 | UI 响应 |
|---|---|---|---|
| `RunnerDisconnected` | — | 失败所有 active session | 推送 |

## 关键设计原则

### 1. 同步优先，事件兜底

聚合根之间**强一致的状态转移走同步调用**——`Issue.StartWorkflow` 直接调 `WorkflowRun.StartAsync`，失败抛回调用方。**事件是事实的"日志"，不是状态转移的"信号"**。

这条原则的反例：把"Issue 启动 workflow"建模成 issue 发事件 → workflow 订阅 → workflow 自动启动。**这会让事务边界模糊、失败无法回退**。

### 2. 跨聚合响应不通过"事件回放"实现

Issue 状态机不订阅 `WorkflowRun.completed` 事件来让自己变 Done。**两个聚合根是平等的**——`WorkflowGrain` 知道 workflow 跑完时**直接调** `IssueGrain` 推动 issue 状态。这样：
- 不变式检查在 issue 自己内部，不依赖外部状态
- 失败抛异常，调用方（workflow grain）走自己的错误路径
- 没有"事件没收到"导致 issue 卡住的 reconciliation

### 3. Issue 域事件 vs Workflow 域事件：纯事实 vs 业务编排

Issue 事件只描述 issue 自身的变化。Workflow 事件描述 workflow 自身的变化。**两边**都可能因为"工作流跑完"产生事件，但**它们是独立事实**：

- `WorkflowRunCompleted` —— workflow 视角："我跑完了"
- `IssueWorkCompleted` —— issue 视角："我作为 issue 完成了"

两边通过同步调用建立因果关系，**不**通过事件订阅建立。

### 4. 同一事实不重复建模

如果某件事已经通过同步调用 A→B 完成，**B 的 transition 自己发自己的事件**。B 不应该订阅 A 的事件再"补发"自己的事件。

### 5. 失败容忍边界

| 失败点 | 谁来兜底 |
|---|---|
| 同步调用 `Workflow.StartAsync` 抛异常 | 调用方（`IssueGrain`）catch，issue 状态机不回退，错误上抛给 HTTP 路径 |
| 同步调用 `Issue.CompleteWork` 抛异常 | 调用方（`WorkflowGrain`）catch，workflow 自己的 transaction commit 仍生效 |
| 事件持久化失败（store 抛异常） | publish-after-commit 路径抛异常回退到调用方——但**state 已 commit**，调用方会 retry，事件因此可能重复（idempotent） |
| 事件总线分发失败 | 仅记 LogError，事件表已写入即事实存在；后续 reconciliation 重新读取分发 |
| 事件订阅方 handler 抛异常 | bus 隔离 per-handler 错误，不影响其他订阅方 |

## 跨聚合交互图

### Issue 启动 workflow

```
Issue 视角                    Workflow 视角
─────────                    ─────────────
1. HTTP POST /start
   │
   ▼
2. Issue 聚合根 holder
   │
   ├── Issue.StartWorkflow(wrId)        ← transition 记录事件
   │
   ├── Workflow 聚合根 holder.Start     ← 同步调用
   │   │
   │   ├── 校验 prerequisites / repo
   │   ├── WorkflowRun.Start
   │   │   └── 返回 WorkflowEvent.WorkflowRunStarted
   │   │
   │   └── 提交边界
   │       ├── 持久化 WorkflowRun
   │       ├── 持久化 + 发布 WorkflowEvent.WorkflowRunStarted
   │       └── 执行该事件的反应（On handler）
   │
   ├── 持久化 Issue + 发布 Issue 域事件
   │
   └── 返回 wrId
```

两个聚合**都发自己的事件**，互不依赖对方的事件。

### Workflow 终结 → Issue 收尾

```
Workflow 视角                          Issue 视角
────────────                          ──────────
1. WorkflowRun.Complete
   └── 返回 [WorkflowEvent.WorkflowRunCompleted]
   │
2. Workflow 聚合根 holder 提交边界
   │
   ├── 持久化 WorkflowRun
   │
   ├── 持久化 + 发布 WorkflowEvent.WorkflowRunCompleted
   │
   └── 执行该事件的反应
       │
       ├── run completion hooks
       │
       └── Issue 聚合根 holder.CompleteWork   ← 同步调用
           │
           ├── Issue.Complete(wrId)
           │
           ├── 持久化 Issue + 发布 Issue 域事件
           │
           └── 返回
```

**关键**：workflow 不知道自己"完成后还会驱动 issue 状态机"——这是 workflow 聚合根 holder 的**编排**逻辑，不是 `WorkflowRun` 聚合根的责任。`WorkflowRun` 只负责"产生事实 + 返回事实"，**不**知道事实会被用来做什么。

### Runner 断开 → AgentSession 失败

```
Runner 视角                         AgentSession 视角
──────────                         ────────────────
1. SignalR 连接断开
   │
2. Hub.OnDisconnected
   │
   ├── connection tracker 注销
   │
   └── publish(RunnerDisconnected)         ← 唯一发布者
       │
       ▼
   bus
       │
       ▼
   session bridge 订阅方
       │
       ├── 查该 runner 的 active sessions
       │
       └── 逐个 session.fail
```

## Issue 状态变化触发的副作用

有些 Issue 事件理应触发副作用，但**当前没有订阅方**——产品决策上**不强求**事件驱动：

| 事件 | 期望副作用 | 当前实现 |
|---|---|---|
| `IssueArchived` | 清理 worktree | HTTP archive 路径内**同步**调 git cleanup（不订阅事件） |
| `IssueCreated` | 搜索索引 | 暂未实现 |
| `IssuePrerequisiteAdded` | 重算反向依赖 | 暂未实现 |

**这些副作用的当前实现不走事件**——是为了让"原子操作"在一个 HTTP 路径内完成（archive + cleanup 必须都成功或都失败）。

如果未来这些副作用要**异步化**（例如 `IssueArchived` 后 5 分钟才清理），再引入对应的 handler 订阅。

## 不变式

1. **同步调用有强一致保证**——失败抛异常，调用方知道要处理
2. **事件是"事实已发生"的不可变日志**——订阅方按需响应
3. **同源事实不重复**——`IssueWorkCompleted` 与 `WorkflowRunCompleted` 是两个独立事实，不是同一个事实的两个 view
4. **事件总线的失败不破坏已有事实**——store 是 outbox 形式的真相，bus 是通知层
5. **跨聚合反应通过同步调用**——不通过订阅对方的事件

## 文档边界

本文描述**跨聚合交互**。单个聚合的事件集合、状态机、不变式在 domain model 中——`WorkflowEvent` 变体与 `IssueEvent` 变体分别在各自聚合根的 domain namespace 下，作为 union 类型自我描述。

事件基础设施（envelope / bus / store / subscription）在 `eventbus.md`。

## Open Questions

1. **Runner 视角事件**：当前 `RunnerDisconnected` 唯一来源是 SignalR OnDisconnected。**主动 disconnect**（runner 调 `POST /api/runner/{id}/unregister`）也应该发吗？
2. **跨域事件追溯**：当 `IssueWorkCompleted` 和 `WorkflowRunCompleted` 同时存在，怎么给 UI 一个统一的"this issue 完成"信号？当前读端 API 合并按 time 排序，但**因果关系**没显式记录（X precedes Y 不代表 X caused Y）。
3. **Outbox relay**：`IssueEvents` / `WorkflowRunEvents` 表是事实上的 outbox，但缺少"已写表但未 publish"的重投机制。如果需要，事件表加 `PublishedAt` 列 + reconciliation timer。
