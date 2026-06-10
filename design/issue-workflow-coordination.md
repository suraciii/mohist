---
purpose: "Business command interactions between the issue, workflow, and runner lifecycles — call graphs, event reactions, and failure ownership per command."
include:
  - "Business commands (Start, Approve/Reject, Report, Stop, Archive, Unarchive, Reopen)."
  - "Swimlane diagrams showing which role calls which role."
  - "Which facts flow over the bus vs which are synchronous calls."
  - "Failure ownership per command."
exclude:
  - "Event variant lists; see the domain model."
  - "Envelope, store, and subscription plumbing; see eventbus.md."
  - "Domain method signatures and class internals."
  - "HTTP API surface; see architecture.md."
style:
  - "Prefer swimlane diagrams over prose."
  - "Keep text short and human-readable."
  - "Use lifecycle role names (issue holder, workflow holder, runner, runner process) as swimlane labels."
---

# Issue, Workflow, and Runner Coordination

本文以**业务命令**为骨架，描述三个生命周期（issue / workflow / runner）之间的交互。每条命令展开成：调用图、事件流向、失败归谁。

## 两条路径

```text
路径 A：同步调用
  强一致的状态转移，调用方知道自己在做什么。
  失败抛异常回退。

路径 B：事件总线
  异步事实广播，订阅方自决。
  事件不携带"应该做什么"。

原则：
  状态转移用 A
  观察/通知用 B
  不混用
```

## Start: User opens an issue for work

```text
HTTP        Issue Holder      Workflow Holder       Bus           UI
  |              |                  |                 |             |
  | POST /start  |                  |                 |             |
  |------------->|                  |                 |             |
  |              | Start            |                 |             |
  |              |----------------->|                 |             |
  |              |                  |                 |             |
  |              |  issue.StartWorkflow(wrId)         |             |
  |              |  (transition + record fact)        |             |
  |              |                  |                 |             |
  |              |                  | validate repo   |             |
  |              |                  |                 |             |
  |              |                  | WorkflowRun.Start            |
  |              |                  |   returns [WorkflowRunStarted]
  |              |                  |                 |             |
  |              |                  | persist+publish|             |
  |              |                  |---------------->|------------>|
  |              |                  |                 |             |
  |              | persist+publish  |                 |             |
  |              |---------------->|                 |------------>|
  |              |                 |                 |             |
  | return wrId  |                 |                 |             |
  |<-------------|                 |                 |             |
```

**Facts**: `WorkflowRunStarted` + `IssueWorkStarted`

**Failure**:
- workflow 侧 Start 抛异常 → issue 状态机不回退，错误上抛 HTTP
- issue 持久化失败 → 整个命令回退

## Approve / Reject: User resolves a stage approval

```text
HTTP        Workflow Holder     Bus          UI
  |              |                |           |
  | POST approve |                |           |
  |------------->|                |           |
  |              |                |           |
  |              | validate stage |           |
  |              |                |           |
  |              | WorkflowRun.ResolveApproval
  |              |   returns [StageApprovalResolved, StageStarted, ?StageApprovalRequested]
  |              |                |           |
  |              | persist+publish|           |
  |              |--------------->|---------->|
  |              |                |           |
  | 200          |                |           |
  |<-------------|                |           |
```

**Facts**: `StageApprovalResolved` + `StageStarted` (+ maybe `StageApprovalRequested` if next stage needs approval)

**Failure**:
- validate 失败 → 409 Conflict
- 持久化失败 → 500

## Report: Runner reports work result

**调用图**（成功路径）：

```text
Runner        Runner Holder    Workflow Holder      Issue Holder      Bus          UI
Process                       (grain)               (grain)            |           |
  |               |                |                    |              |           |
  | ReportResult  |                |                    |              |           |
  |-------------->|                |                    |              |           |
  |               | forward        |                    |              |           |
  |               |--------------->|                    |              |           |
  |               |                |                    |              |           |
  |               |                | validate lease     |              |           |
  |               |                |                    |              |           |
  |               |                | WorkflowRun.ReportResult            |           |
  |               |                |   returns [TaskCompleted,            |           |
  |               |                |            StageCompleted,           |           |
  |               |                |            CheckPassed,             |           |
  |               |                |            StageStarted,            |           |
  |               |                |            WorkflowRunCompleted]    |           |
  |               |                |                    |              |           |
  |               |                | persist+publish    |              |           |
  |               |                |----------------------------------->|---------->|
  |               |                |                    |              |           |
  |               |                | reaction: WorkflowRunCompleted     |           |
  |               |                |------------------->|              |           |
  |               |                |                    |              |           |
  |               |                |                    | issue.Complete(wrId)     |
  |               |                |                    | persist+publish          |
  |               |                |                    |------------->|----------->|
  |               |                |                    |              |           |
  |               |  report resp   |                    |              |           |
  |<--------------|<---------------|                    |              |           |
```

**失败路径**（`ReportResult` 内部 fail）：

```text
Runner        Runner Holder    Workflow Holder      Issue Holder      Bus
Process                       (grain)               (grain)            |
  |               |                |                    |              |
  | ReportResult  |                |                    |              |
  |-------------->|                |                    |              |
  |               | forward        |                    |              |
  |               |--------------->|                    |              |
  |               |                |                    |              |
  |               |                | returns [TaskFailed, StageFailed,  |
  |               |                |          WorkflowRunFailed]         |
  |               |                |                    |              |
  |               |                | persist+publish    |              |
  |               |                |----------------------------------->|
  |               |                |                    |              |
  |               |                | reaction: WorkflowRunFailed        |
  |               |                |------------------->|              |
  |               |                |                    |              |
  |               |                |                    | issue.AbortWorkflow(wrId, reason)
  |               |                |                    | persist+publish
  |               |                |                    |------------->|
```

**Facts**:
- 成功: `TaskCompleted` / `StageCompleted` / `CheckPassed` / `StageStarted` / `WorkflowRunCompleted` + `IssueWorkCompleted`
- 失败: `TaskFailed` / `StageFailed` / `WorkflowRunFailed` + `IssueWorkAborted`

**关键不变量**:
- workflow 终结时**同步**调 issue holder — 不订阅事件
- 调用方 (workflow holder) 失败回退清晰

**Failure**:
- workflow 内部 advance 失败 → 整个 Report 命令失败 → runner 收到错误
- issue holder.CompleteWork 抛异常 → 同样回退到 workflow holder，runner 重试

## Stop: User cancels

**两种 case**:

```text
Case A: issue 没在跑 workflow
  HTTP -> Issue Holder -> issue.Close(reason) -> publish IssueClosed -> UI
  无 workflow 侧反应
```

```text
Case B: issue 在跑 workflow

  HTTP        Issue Holder      Workflow Holder       Issue Holder     Bus
  |              |                  |                    |              |
  | POST close   |                  |                    |              |
  |------------->|                  |                    |              |
  |              | Workflow.Stop    |                    |              |
  |              |----------------->|                    |              |
  |              |                  | WorkflowRun.Stop   |              |
  |              |                  |   returns [WorkflowRunStopped]    |
  |              |                  | persist+publish    |              |
  |              |                  |------------------->|------------->|
  |              |                  |                    |              |
  |              |                  | reaction: WorkflowRunStopped     |
  |              |                  | issue.AbortWork (via holder)     |
  |              |                  | (sync call returns)              |
  |              |                  |                  |                |
  |              | issue.Close(reason)                |                |
  |              | persist+publish  |                  |                |
  |              |--------------------------------------|--------------->|
  |              |                  |                  |                |
  | 200          |                  |                  |                |
  |<-------------|                  |                  |                |
```

**Facts**:
- Case A: `IssueClosed`
- Case B: `WorkflowRunStopped` + `IssueWorkAborted` + `IssueClosed`

**关键**: Stop 是**反向**调用——issue holder 主动 stop workflow（**不**订阅 `WorkflowRunStopped` 事件来自我 stop）。`IssueClosed` 在 `IssueWorkAborted` 之后——issue 状态从 `InProgress` → `Cancelled`（via Abort）→ `Cancelled`（via Close）；两次 transition 发两个事实。

## Archive: User archives a done issue

```text
HTTP        Issue Holder       Git             Bus           UI
  |              |               |               |             |
  | POST archive |               |               |             |
  |------------->|               |               |             |
  |              | issue.Archive |               |             |
  |              | (transition + record fact)     |             |
  |              |               |               |             |
  |              | persist+publish              |             |
  |              |------------------------------>|------------>|
  |              |               |               |             |
  |              | git cleanup worktree          |             |
  |              |--------------->|               |             |
  |              |               |               |             |
  | 200 { cleanup }              |               |             |
  |<-------------|               |               |             |
```

**Facts**: `IssueArchived`

**关键不变量**:
- git cleanup 与 archive 在**同一 HTTP 路径**同步完成——原子
- **不**订阅 `IssueArchived` 事件触发 cleanup——避免 archive 成功但 cleanup 失败的中间态

## Unarchive: User un-archives a done issue

```text
HTTP        Issue Holder       Bus           UI
  |              |               |             |
  | POST unarchive               |             |
  |------------->|               |             |
  |              | issue.Unarchive (transition + record fact)
  |              | persist+publish              |
  |              |--------------->|------------>|
  |              |               |             |
  | 200          |               |             |
  |<-------------|               |             |
```

**Facts**: `IssueUnarchived`

**注意**: unarchive **不**重建 worktree。worktree 重建是另一个未来命令。

## Reopen: User reopens a cancelled issue

```text
HTTP        Issue Holder       Bus           UI
  |              |               |             |
  | POST reopen  |               |             |
  |------------->|               |             |
  |              | issue.Reopen  |             |
  |              | persist+publish              |
  |              |--------------->|------------>|
  |              |               |             |
  | 200          |               |             |
  |<-------------|               |             |
```

**Facts**: `IssueReopened`

Reopen 后 issue 状态变回 Backlog。User 可重新发 Start 命令。

## Runner Disconnect

```text
SignalR      Hub              Bus          Session Bridge      Runner Holder
Connection   (OnDisconnected)              (subscriber)        (sessions)
  |              |              |                |                   |
  | drop         |              |                |                   |
  |------------->|              |                |                   |
  |              | unregister   |                |                   |
  |              | (in tracker) |                |                   |
  |              |              |                |                   |
  |              | publish      |                |                   |
  |              | (RunnerDisconnected)         |                   |
  |              |------------->|                |                   |
  |              |              |                |                   |
  |              |              | deliver        |                   |
  |              |              |--------------->|                   |
  |              |              |                |                   |
  |              |              |                | find sessions for runnerId
  |              |              |                |------------------>|
  |              |              |                |                   |
  |              |              |                | session.fail     |
  |              |              |                |<------------------|
```

**Facts**: `RunnerDisconnected`（**唯一**订阅方响应——fail 掉 active session 避免 leak）

**关键不变量**:
- 没有"同步调用方"——HTTP 路径在 disconnect 瞬间无法知道"哪些 session 还在跑这个 runner"
- 走事件让 session bridge 按需查 + 决策
- 这条事件**不**携带"应该做什么"——session bridge 自决 fail 哪些 session

## 命令的非对称观察

```text
命令             同步调用链                事件总线            失败归谁
─────────────────────────────────────────────────────────────────────────
Start            Issue -> Workflow        推 UI              调用方 (issue)
Approve/Reject   (workflow 内部)          推 UI              调用方 (workflow)
Report (成功)    Workflow -> Issue        全程推 UI          workflow
Report (失败)    Workflow -> Issue        全程推 UI          workflow
Stop (未跑)      (issue 内部)             推 UI              issue
Stop (在跑)      Issue -> Workflow ->     推 UI              issue
                       Issue
Archive          (issue 内部)             推 UI              HTTP 路径
Unarchive        (issue 内部)             推 UI              issue
Reopen           (issue 内部)             推 UI              issue
Runner           (无调用方)               Runner -> session  runner / bridge
disconnect                                 bridge
```

**观察**:
- Archive 是**唯一**含"非状态副作用"（git cleanup）的命令——**不走事件**走同步
- Runner disconnect 是**唯一**事件驱动的失败恢复——其他失败都靠同步调用方处理

## 不变式

```text
1. 同步调用 = 状态转移
   A 改变 state 直接导致 B 改变 state

2. 事件总线 = 事实通知
   A 改变 state 让世界知道，但不自动推动其他 state

3. 同源事实不重复
   IssueWorkCompleted 与 WorkflowRunCompleted 是两个独立事实

4. 事件不携带"应该做什么"
   订阅方自决

5. 失败由调用方负责
   同步调用方 catch + 决定 retry 或回退
   事件发布失败仅记日志

6. archive 的 cleanup 同步
   避免 archive 成功但 cleanup 失败的中间态
```

## 设计决策记录

### Decision 1: Report 走"Workflow -> Issue"而不是订阅事件

**否决订阅方案**: 让 issue holder 订阅 `WorkflowRunCompleted` 事件，自己推进 issue state。

```text
否决原因:
  - 不变式检查在 issue 内部，但事件可能丢失
    (silo 重启 / reconciliation delay)
  - 失败时调用方 (workflow holder) 无法重试
    issue state 已经 stale
  - 跨域反应通过同步调用更简单、更可观测
```

**采用**: workflow holder 终结时**同步**调 issue holder.CompleteWork / AbortWork。issue **不**订阅任何 workflow 事件。

### Decision 2: Archive 同步调 worktree cleanup 而不订阅事件

**否决订阅方案**: 让 `IssueArchived` 事件触发 worktree cleanup handler。

```text
否决原因:
  - "archive + cleanup" 必须原子
    (用户期望"归档后 worktree 没了")
  - 异步 cleanup 留下"archive 成功但 worktree 还在"
    的中间态
  - cleanup 失败需要回退 archive
    但事件已发——回退复杂
```

**采用**: HTTP archive 路径同步串联。`IssueArchived` 事件**仅**用于 UI 通知。

### Decision 3: Runner disconnect 走事件

**原因**: 没有同步调用方——HTTP 服务在 disconnect 瞬间无法知道"哪些 session 还在跑这个 runner"。

```text
  走事件让 session bridge 按需查 + 决策
  这条事件没有"应该做什么"硬编码
  session bridge 自决 fail 哪些 session
```

### Decision 4: 不引入 Coordinator 中间层

**否决方案**: 引入 `IssueWorkflowCoordinator` 作为 application service，集中 Start/Approve/Reject/Report 的命令入口。

**当前选择**: 协调逻辑直接由调用方 holder 内联（`Issue holder.Start` 内联 workflow 调用）。不引入中间层。

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
本文       业务命令图、调用链、事件反应、失败归谁
domain     每个生命周期能产生什么事实 (union 变体)
eventbus   envelope / 路由 / 订阅
arch       HTTP API 表面
```

## Open Questions

```text
1. Coordinator 抽取时机
   当 retry / rerun / patch variables 命令需要协调时
   是否引入 IssueWorkflowCoordinator 集中 reconcile 逻辑

2. Runner 主动 disconnect
   POST /api/runner/{id}/unregister
   是否也发 RunnerDisconnected?
   当前可能只 SignalR 隐式 disconnect 发

3. 跨域因果记录
   IssueWorkCompleted + WorkflowRunCompleted 同时存在
   UI 怎么判断"issue 完成因为 workflow 完成"?
   当前没显式 causation 字段，靠 time 排序推断

4. Outbox relay
   事件表是事实上的 outbox
   缺"已写表但未 publish"的重投机制
```
