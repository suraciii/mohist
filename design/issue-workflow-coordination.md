---
purpose: "How aggregates (Issue, Workflow, Runner, Session) interact. Documents the cross-aggregate impact of each business command, distinguishing synchronous commands from events that cause a state change in another aggregate."
include:
  - "Business commands as sequences of aggregate transitions."
  - "Which aggregate calls which aggregate (synchronous)."
  - "Events that cause another aggregate to transition (asynchronous)."
  - "Failure ownership per command."
exclude:
  - "Events that have no cross-aggregate effect (Web UI / notification subscribers only)."
  - "Event variant lists; see the domain model."
  - "Domain method signatures and class internals."
  - "Transport / plumbing (HTTP, SignalR, bus)."
style:
  - "Use aggregate names (Issue, Workflow, Runner, Session) as actors."
  - "Vertical line = aggregate; horizontal line = interaction (command or event)."
  - "Solid arrow = synchronous command. Dashed arrow = event with cross-aggregate impact."
  - "Only draw events that cause a state change in another aggregate."
  - "Pure observation events (UI, notifications) are not drawn."
  - "Prefer diagrams over prose."
  - "Keep text short; let diagrams carry the explanation."
---

# Aggregate Coordination

## 跨聚合影响分析

按"事件的影响"约定，列出每个聚合事件，判断它是否驱动另一聚合的状态变化：

| Sender | Event | 跨聚合影响 |
|---|---|---|
| Issue | `IssueCreated` / `LabelsChanged` / `PriorityChanged` / `PrerequisiteAdded` / `PrerequisiteRemoved` | 无 |
| Issue | `IssueWorkStarted` / `IssueWorkCompleted` / `IssueWorkAborted` | 无 — 由同步调用 Workflow 触发 |
| Issue | `IssueClosed` / `IssueArchived` / `IssueUnarchived` / `IssueReopened` | 无 |
| Workflow | 17 个 `Workflow*` 事件 | 无 |
| Runner | `RunnerDisconnected` | **有** → Session 收尾 |

**结论**: mohist 当前**所有跨聚合状态变化**通过**同步命令**驱动。**唯一事件驱动**的反应是 `RunnerDisconnected` → `Session`。

```text
跨聚合驱动机制:
  99% 同步命令 (Issue ↔ Workflow, Runner → Workflow)
  1%  事件       (Runner → Session via RunnerDisconnected)
```

为什么 99% 走同步命令：
- 强一致：调用方知道结果；失败抛回
- 失败责任清晰：调用方负责 retry / 回退
- 调试容易：一次调用一个因果链

为什么 Runner disconnect 走事件：
- 没有同步调用方——没有聚合能在 disconnect 瞬间知道"哪些 session 还在跑这个 runner"
- session 自决 fail 哪些 session；runner 不指示

## Start: User opens an issue for work

```text
Issue              Workflow
  |                   |
  |--- start ------->|
```

**Facts** (由同步调用引发): `IssueWorkStarted` / `WorkflowRunStarted`

**失败**:
- issue 校验失败（前提/仓库）→ 整个命令回退，issue 不 transition
- workflow transition 抛异常 → issue 已 transition，**不回退**；错误上抛
  - 当前实现选择——issue 与 workflow 跨聚合不通过事务协调
  - 未来可考虑用 saga / outbox 模式回退 issue

## Approve / Reject: User resolves a stage approval

无跨聚合交互。workflow 内部 transition。

**Facts**: `StageApprovalResolved` / `StageStarted` / 可能 `StageApprovalRequested`

**失败**:
- 校验失败（当前 stage 不等待审批）→ 抛异常
- 持久化失败 → 整个命令回退

## Report: Runner reports work result

**成功路径**:

```text
Runner         Workflow              Issue
  |               |                   |
  |--- report -->|                   |
  |               |                   |
  |               |--- complete ----->|
```

**失败路径**:

```text
Runner         Workflow              Issue
  |               |                   |
  |--- report -->|                   |
  |               |                   |
  |               |--- abortWorkflow >|
```

**Facts**:
- 成功: `TaskCompleted` / `StageCompleted` / `CheckPassed` / `WorkflowRunCompleted` + `IssueWorkCompleted`
- 失败: `TaskFailed` / `StageFailed` / `WorkflowRunFailed` + `IssueWorkAborted`

**关键不变量**: workflow 终结时**同步**调 issue，**不**订阅事件。

**失败**:
- workflow 内部 advance 失败 → 整个 Report 命令失败 → runner 收到错误
- issue transition 抛异常 → 同样回退到 workflow，runner 重试

## Stop: User cancels

**Case A: issue 没在跑 workflow**: 单一聚合 transition，不画图。

**Case B: issue 在跑 workflow**:

```text
Issue              Workflow
  |                   |
  |--- stop --------->|
```

**Facts**: `WorkflowRunStopped` / `IssueWorkAborted` / `IssueClosed`

**关键**: Stop 是**反向**调用——issue 主动 stop workflow，**不**订阅 `WorkflowRunStopped` 事件来自我 abort。

## Archive: User archives a done issue

无跨聚合交互。issue 内部 transition + 命令内部副作用（worktree cleanup 同步串联）。

**Facts**: `IssueArchived`

**关键不变量**:
- worktree cleanup 与 archive 在**同一命令**中完成——原子
- **不**订阅 `IssueArchived` 事件触发 cleanup
- cleanup 是命令的副作用，不是聚合之间的协调；依赖关系在 command handler 内部

## Unarchive: User un-archives a done issue

无跨聚合交互。issue 内部 transition。

**Facts**: `IssueUnarchived`

**注意**: unarchive **不**重建 worktree。worktree 重建是另一个未来命令。

## Reopen: User reopens a cancelled issue

无跨聚合交互。issue 内部 transition。

**Facts**: `IssueReopened`

Reopen 后 issue 状态变回 Backlog。User 可重新发 Start 命令。

## Runner Disconnect: 唯一有跨聚合影响的事件

```text
Runner            Session
  |                 |
  · ··> RunnerDisconnected
  |                 |
                    | transition
                    | fail (per session)
```

**关键**:
- 没有同步调用方——没有聚合能在 disconnect 瞬间知道"哪些 session 还在跑这个 runner"
- Session **自决** fail 哪些 session（runner 不指示）

## 命令的非对称观察

| 命令 | 同步链 | 失败归谁 |
|---|---|---|
| Start | Issue → Workflow | issue |
| Approve/Reject | (workflow 内部) | workflow |
| Report (成功) | Workflow → Issue | workflow |
| Report (失败) | Workflow → Issue | workflow |
| Stop (未跑) | (issue 内部) | issue |
| Stop (在跑) | Issue → Workflow | issue |
| Archive | (issue 内部) | issue |
| Unarchive | (issue 内部) | issue |
| Reopen | (issue 内部) | issue |
| Runner disconnect | (event → Session) | session |

**观察**:
- Archive 是**唯一**含"非状态副作用"（worktree cleanup）的命令——**不走事件**走命令内部
- Runner disconnect 是**唯一**事件驱动的失败恢复——其他失败都靠同步调用方处理

## 不变式

```text
1. 跨聚合状态变化 = 同步命令
   Issue.WorkStart 调 Workflow.Start (不是订阅 IssueWorkStarted)

2. 事件仅在"驱动另一聚合状态变化"时使用
   当前仅 RunnerDisconnected 一例
   纯观察事件 (Web UI 通知) 不在本文档

3. 同源事实不重复
   IssueWorkCompleted 与 WorkflowRunCompleted 是两个独立事实

4. 事件不携带"应该做什么"
   订阅方自决 (RunnerDisconnected 案例)

5. 失败由调用方负责
   同步调用方 catch + 决定 retry 或回退
   事件发布失败仅记日志

6. archive 的 cleanup 与 archive 命令原子
   避免 archive 成功但 cleanup 失败的中间态
```

## 设计决策记录

### Decision 1: Report 走 "Workflow → Issue" 而不订阅事件

**否决订阅方案**: 让 issue 订阅 `WorkflowRunCompleted` 事件，自己推进 issue state。

```text
否决原因:
  - 事件可能丢失 (silo 重启 / reconciliation delay)
  - 失败时调用方 (workflow) 无法重试
    issue state 已经 stale
  - 跨聚合反应通过同步调用更简单、更可观测
```

**采用**: workflow 终结时**同步**调 issue。issue **不**订阅任何 workflow 事件。

### Decision 2: Archive 同步调 worktree cleanup 而不订阅事件

**否决订阅方案**: 让 `IssueArchived` 事件触发 worktree cleanup。

```text
否决原因:
  - "archive + cleanup" 必须原子
  - 异步 cleanup 留下"archive 成功但 worktree 还在"
    的中间态
  - cleanup 失败需要回退 archive
    但事件已发——回退复杂
```

**采用**: archive 命令原子串联。`IssueArchived` 事件**仅**用于观察。

### Decision 3: Runner disconnect 走事件

**原因**: 没有同步调用方——没有聚合能在 disconnect 瞬间知道"哪些 session 还在跑这个 runner"。

```text
  走事件让 Session 自决
  Runner 不指示 Session 做什么
```

### Decision 4: 不引入 Coordinator 中间层

**否决方案**: 引入协调者聚合，集中 Start/Approve/Reject/Report 的命令入口。

**当前选择**: 协调逻辑直接由调用方聚合内联。

```text
潜在问题:
  当命令路径增加 (retry / rerun / patch variables)
  时，协调逻辑可能重复

当前选择的原因:
  简单命令图下中间层不必要
  未来值得抽出 coordinator
```

## 文档边界

```text
本文       聚合之间如何交互 (sync commands + 有影响的事件)
domain     每个聚合能产生什么事实 (union 变体)
eventbus   事件基础设施 (envelope / 路由 / 订阅)
其他       传输层 (HTTP / SignalR) 属于实现细节
           纯观察事件 (Web UI 通知) 不在领域交互中表示
```

## Open Questions

```text
1. Coordinator 抽取时机
   当 retry / rerun / patch variables 命令需要协调时
   是否引入协调者聚合集中 reconcile 逻辑

2. Runner 主动 disconnect
   显式 unregister 是否也发 RunnerDisconnected?
   当前可能只隐式 disconnect 发

3. 跨域因果记录
   IssueWorkCompleted + WorkflowRunCompleted 同时存在
   怎么判断"issue 完成因为 workflow 完成"?
   当前没显式 causation 字段，靠时间顺序推断

4. 事件重投
   事件表是事实上的 outbox
   缺"已写表但未 publish"的重投机制
```
