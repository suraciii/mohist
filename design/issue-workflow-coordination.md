---
purpose: "How aggregates (Issue, WorkflowRun, Runner, Session) interact. Documents the cross-aggregate impact of each business command, distinguishing synchronous commands from events that trigger a downstream command."
include:
  - "Business commands as sequences of aggregate transitions."
  - "Which aggregate calls which aggregate (synchronous)."
  - "Events that trigger a downstream aggregate command (asynchronous)."
  - "Failure ownership per command."
exclude:
  - "Events that have no cross-aggregate impact (Web UI / notification subscribers only)."
  - "Event variant lists; see the domain model."
  - "Domain method signatures and class internals."
  - "Transport / plumbing (HTTP, SignalR, bus)."
style:
  - "Use aggregate names (Issue, WorkflowRun, Runner, Session) as actors."
  - "Vertical line = aggregate; horizontal line = interaction."
  - "Solid arrow = synchronous command. Dashed arrow = event."
  - "Event-triggered commands share the same source aggregate (drawn as a solid line under the event)."
  - "Commands are named without the Async suffix or parameter list."
  - "Only draw events that trigger a cross-aggregate command."
  - "Pure observation events (UI, notifications) are not drawn."
  - "Single-aggregate transitions are listed in prose, not drawn."
  - "Prefer diagrams over prose."
  - "Keep text short; let diagrams carry the explanation."
---

# Aggregate Coordination

## 三个元素

```text
聚合      vertical line
命令      实线箭头, 跨聚合同步调用
事件      虚线箭头, 触发下游聚合的命令
```

事件触发的命令**与事件同源**（都从发送方聚合的竖线出发）—— 虚线在前（事件触发），实线在后（被触发的命令）。

## 跨聚合事件→命令清单

| 事件 | 发送方 | 触发的命令 | 接收方 |
|---|---|---|---|
| `WorkflowRunCompleted` | WorkflowRun | `CompleteWork` | Issue |
| `WorkflowRunFailed` | WorkflowRun | `AbortWork` | Issue |
| `RunnerDisconnected` | Runner | (无 — Session 自决 fail) | Session |

**单一聚合 transition**（不画图, 文字列出）：

```text
WorkflowRun 内部: Pause / Resume / Approve / Reject / Retry / Rerun
Issue 内部:      Archive / Unarchive / Reopen / Close
Runner 内部:     Register / Unregister / Heartbeat
```

## Start: User opens an issue for work

```text
Issue              WorkflowRun
  |                   |
  |--- StartWork ---->|
```

**Facts** (由同步调用引发): `IssueWorkStarted` / `WorkflowRunStarted`

**失败**:
- issue 校验失败（前提/仓库）→ 整个命令回退，issue 不 transition
- workflow 持久化+发事件**之后**抛异常 → issue 已 transition，**不回退**；错误上抛
  - 当前实现选择——issue 与 workflow 跨聚合不通过事务协调
  - 未来可考虑用 saga / outbox 模式回退 issue

## Report: Runner reports work result

**成功路径**:

```text
Runner         WorkflowRun           Issue
  |               |                   |
  |--- Report --->|                   |
  |               |                   |
  |               · ··> WorkflowRunCompleted
  |               |                   |
  |               |--- CompleteWork ->|
```

**失败路径**:

```text
Runner         WorkflowRun           Issue
  |               |                   |
  |--- Report --->|                   |
  |               |                   |
  |               · ··> WorkflowRunFailed
  |               |                   |
  |               |--- AbortWork ---->|
```

**Facts**:
- 成功: `TaskCompleted` / `StageCompleted` / `CheckPassed` / `WorkflowRunCompleted` + `IssueWorkCompleted`
- 失败: `TaskFailed` / `StageFailed` / `WorkflowRunFailed` + `IssueWorkAborted`

**关键不变量**:
- workflow 持久化+发事件**之后**触发命令（per `IWorkflowCompletionHook` 约定）
- 命令执行失败由 hook 隔离, 不阻断 workflow 报告路径
- hook 本身保持幂等（completion 由 `workflowRunId` 防重）

**失败**:
- workflow 内部 advance 失败 → 整个 Report 命令失败 → runner 收到错误
- issue transition 抛异常 → 同样回退到 workflow，runner 重试

## Stop: User cancels

**Case A: issue 没在跑 workflow**: 单一聚合 transition (Close), 不画图。

**Case B: issue 在跑 workflow**:

```text
Issue              WorkflowRun
  |                   |
  |--- Cancel ------->|
```

`Cancel` 内部链路: `IssueGrain.CancelAsync` → `WorkflowGrain.StopAsync("issue-closed")` → `Issue.Close("user-cancelled")` (同一 Issue 内)。

**关键**: Stop 是**反向**调用——issue 主动 stop workflow, **不**订阅 `WorkflowRunStopped` 事件来自我 abort。

## Archive / Unarchive / Reopen: 单一聚合 transition

不涉及跨聚合交互, 不画图。

**关键不变量**:
- Archive 命令**原子**串联 worktree cleanup——**不**订阅 `IssueArchived` 事件触发 cleanup
- Unarchive **不**重建 worktree
- Reopen 后 issue 状态变回 Backlog

## Runner Disconnect: 唯一当前有跨聚合影响的事件

```text
Runner            Session
  |                 |
  · ··> RunnerDisconnected
  |                 |
                    | (fail sessions)
```

**关键**:
- 没有同步调用方——没有聚合能在 disconnect 瞬间知道"哪些 session 还在跑这个 runner"
- Session **自决** fail 哪些 session (runner 不指示)
- 不画下游实线: Session fail 是单聚合 transition, 不是跨聚合命令

## 命令的非对称观察

| 命令 | 同步链 / 事件→命令 | 失败归谁 |
|---|---|---|
| Start | Issue → WorkflowRun (sync) | issue |
| Approve/Reject | (workflowRun 内部) | workflowRun |
| Report (成功) | WorkflowRunCompleted → CompleteWork | workflowRun |
| Report (失败) | WorkflowRunFailed → AbortWork | workflowRun |
| Stop (未跑) | (issue 内部 Close) | issue |
| Stop (在跑) | Issue → WorkflowRun (sync) | issue |
| Archive | (issue 内部) | issue |
| Unarchive | (issue 内部) | issue |
| Reopen | (issue 内部) | issue |
| Runner disconnect | (RunnerDisconnected → Session 自决) | session |

**观察**:
- Archive 是**唯一**含"非状态副作用"（worktree cleanup）的命令——**不走事件**走命令内部
- Runner disconnect 是**唯一**事件驱动的失败恢复——其他失败都靠同步调用方处理

## 不变式

```text
1. 跨聚合状态变化 = 同步命令
   Issue.StartWork 调 WorkflowRun.Start (不是订阅 IssueWorkStarted)

2. 事件 = 跨聚合命令的触发器
   事件触发的命令与事件同源 (都从同一发送方出发)
   接收方收到事件后转换为对另一聚合的命令

3. 纯观察事件不在本文档
   Web UI / 通知订阅是实现细节, 不在领域交互中表示

4. 单一聚合 transition 不画图
   只在文字列出方法名

5. 同源事实不重复
   IssueWorkCompleted 与 WorkflowRunCompleted 是两个独立事实

6. 失败由调用方负责
   同步命令调用方 catch + 决定 retry 或回退
   事件发布失败仅记日志
   hook 失败由 hook 隔离, 不阻断 workflow

7. archive 的 cleanup 与 archive 命令原子
   避免 archive 成功但 cleanup 失败的中间态
```

## 设计决策记录

### Decision 1: Report 走事件→命令, 由 hook 触发

**采用**: workflow 持久化+发 `WorkflowRunCompleted` **之后**调 `IWorkflowCompletionHook.OnWorkflowCompletedAsync`。
宿主 `IssueWorkflowCompletionHook` 看到 `correlation.EntityType == "issue"` → 调 `IssueGrain.CompleteWorkflowAsync`。

```text
原因:
  - hook 失败不阻断 workflow 报告路径 (per-hook 隔离)
  - hook 本身保持幂等 (completion 由 workflowRunId 防重)
  - WorkflowGrain 只负责触发, 不感知 Issue 业务
  - Issue 业务由宿主层 IssueWorkflowCompletionHook 承载
```

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

**否决方案**: 引入协调者聚合, 集中 Start/Approve/Reject/Report 的命令入口。

**当前选择**: 协调逻辑直接由调用方聚合内联。

```text
潜在问题:
  当命令路径增加 (retry / rerun / patch variables)
  时, 协调逻辑可能重复

当前选择的原因:
  简单命令图下中间层不必要
  未来值得抽出 coordinator
```

### Decision 5: 精确聚合名 WorkflowRun (非 Workflow 简写)

**原因**: 与领域事件 `WorkflowRunCompleted` / `WorkflowRunFailed` / `WorkflowRunStarted` 等命名一致, 避免与 `Workflow` 配置概念混淆 (workflow template 不属于聚合)。

## 文档边界

```text
本文       聚合之间如何交互 (sync commands + 事件触发的命令)
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
   当前没显式 causation 字段, 靠时间顺序推断

4. 事件重投
   事件表是事实上的 outbox
   缺"已写表但未 publish"的重投机制

5. IWorkflowCompletionHook 实现迁移
   memory 已记录目标架构, 当前 WorkflowGrain.OnWorkflowCompletedAsync
   仍是 private 内部方法, 未接入 hook 抽象
   本文按目标架构画, 实现差异记录在 issue 跟踪
```
