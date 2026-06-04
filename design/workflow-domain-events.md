---
purpose: "Design how WorkflowRun produces workflow events and how WorkflowGrain reacts to committed workflow facts."
include:
  - "WorkflowRun event boundary."
  - "Domain method result shape."
  - "Commit boundary and reactions."
exclude:
  - "Runner assignment and backlog polling; see workflow-scheduling.md."
  - "Database schema and migration details."
  - "HTTP API, Web UI payloads, and agent session transcript events."
---

# Workflow Events

本文设计 WorkflowRun 产生的 workflow events。

核心目标：WorkflowRun 决定状态变化时，同时产出有意义的 workflow facts；WorkflowGrain 在这些事实提交后，再执行日志、live event、锁释放、调度和 hook 等 reactions。

## Problem

当前 WorkflowGrain 在多个路径中手动串起这些动作：

```text
mutate WorkflowRun
save state
append workflow event
emit live event
release stage lock
resume scheduling
run completion hook
```

问题不是代码重复本身，而是事实来源不清晰。

一个 stage 可以在 WorkflowRun 中变成 `Completed` 或 `Failed`，但后续行为取决于外层 orchestration path 是否记得补齐所有副作用。新的 transition 很容易漏掉 audit event、projection update、lock release 或 downstream reaction。

## Design Invariant

```text
Task executes.
Check verifies.
Runner reports.
WorkflowRun decides.
Workflow events explain what WorkflowRun decided.
```

WorkflowRun method 必须负责：

- 校验 command 是否符合当前状态。
- 修改 WorkflowRun 状态。
- 返回它刚刚决定的领域事实。

WorkflowGrain 必须负责：

- 校验 runner ownership、lease、外部调用条件。
- 持久化 WorkflowRun。
- 将 committed workflow events 投影为 workflow event rows 和 live events。
- 对 committed workflow events 执行 orchestration reactions。

事件归属从 `WorkflowRun.Metadata` 读取：

```text
workflowRunId -> WorkflowRun.Metadata(ProjectId, IssueId)
```

不要从 runtime context 或 profile variables 反查 project/issue。

## Event Boundary

Workflow event 是 WorkflowRun 能凭自身状态和方法输入决定的业务事实。

应该是 workflow event：

- workflow run 生命周期边界。
- stage 生命周期边界。
- approval 请求或处理结果。
- task/check 的有意义结果。
- repair 被 workflow rule 安排。

不应该是 workflow event：

- runner assigned/rejected。
- stage lock acquired/released/waiting。
- next work calculated。
- workflow event row persisted。
- SSE/live event published。
- EF、Orleans、HTTP、UI、runner adapter 的实现细节。

## Event Types

事件类型不带 `Domain` 后缀。

当前实现使用 C# `union`，让事件集合成为封闭集合。新增事件时，持久化映射和 Grain reaction 的 switch expression 必须显式处理，否则编译失败。

```csharp
public union WorkflowEvent(
    WorkflowRunStarted,
    WorkflowRunResumed,
    WorkflowRunPaused,
    WorkflowRunStopped,
    WorkflowRunCompleted,
    WorkflowRunFailed,
    StageStarted,
    StageCompleted,
    StageFailed,
    StageApprovalRequested,
    StageApprovalResolved,
    TaskCompleted,
    TaskFailed,
    CheckPassed,
    CheckFailed,
    CheckPending,
    RepairScheduled);

public sealed record WorkflowRunStarted;
public sealed record WorkflowRunResumed;
public sealed record WorkflowRunPaused;
public sealed record WorkflowRunStopped;
public sealed record WorkflowRunCompleted;
public sealed record WorkflowRunFailed(string? Message);

public sealed record StageStarted(string Stage);
public sealed record StageCompleted(string Stage);
public sealed record StageFailed(string Stage, string? Reason);

public sealed record StageApprovalRequested(string Stage);
public sealed record StageApprovalResolved(string Stage, ApprovalResult Result, string? Reason = null);

public sealed record TaskCompleted(string Stage, string TaskId);
public sealed record TaskFailed(string Stage, string TaskId, string? Message);

public sealed record CheckPassed(string Stage, string CheckName, string? Message);
public sealed record CheckFailed(string Stage, string CheckName, string? Message);
public sealed record CheckPending(string Stage, string CheckName, string? Message);
public sealed record RepairScheduled(string Stage, string CheckName, IReadOnlyList<string> TaskIds);
```

`Retry`、`Rerun`、`Approve` 是 command，不是 event。它们可能产生 `WorkflowRunResumed`、`StageStarted`、`StageApprovalResolved` 等事实。

## Working Model

### Domain Produces Events

```csharp
var events = run.CompleteTask(taskResult);
```

WorkflowRun method 做三件事：

```text
validate command
mutate WorkflowRun state
return produced workflow events
```

事件不挂在 WorkflowRun 上，不作为 pending state 持久化。

```csharp
public IReadOnlyList<WorkflowEvent> CompleteTask(TaskResult result)
{
    var events = new List<WorkflowEvent>();

    events.Add(new TaskCompleted(...));
    events.AddRange(Advance());

    return events;
}
```

`Advance` 返回 stage/run 边界事件。一次 WorkflowRun method 可能产生多个事实：

```text
CompleteTask
  -> TaskCompleted
  -> StageCompleted
  -> StageStarted(next stage)
```

调用者不通过 before/after status diff 推断事件。

### Grain Reacts To Events

WorkflowGrain 调用 WorkflowRun method 后，只拿到本次 transition 产生的 events：

```csharp
var events = _run.CompleteTask(taskResult);
await CommitAsync(events);
```

`CommitAsync` 是统一提交边界。它不通过全局 bus 让 WorkflowGrain 再订阅自己。

```csharp
private async Task CommitAsync(
    IReadOnlyList<WorkflowEvent> events)
{
    await SaveRunAsync();
    await PersistAndPublishAsync(events);

    foreach (var e in events)
        await On(e);
}
```

所有 events 都走通用处理：

```text
persist workflow event row
publish live event
```

持久化和发布使用 `workflowRunId` 和 `issueId`。如果外层 API 仍用 issue number，先在 API/query 边界解析为 `issueId`。`ResourceKey` 暂时只作为 URL/resource-path 约定。

业务编排只显式响应 WorkflowGrain 关心的事件：

```csharp
private Task On(WorkflowEvent e) =>
    e switch
    {
        WorkflowRunStarted => OnWorkflowStartedAsync(),
        WorkflowRunResumed => OnWorkflowResumedAsync(),
        WorkflowRunPaused => DisableWorkHeartbeatAsync(),
        WorkflowRunStopped => DisableWorkHeartbeatAsync(),
        WorkflowRunFailed => DisableWorkHeartbeatAsync(),
        WorkflowRunCompleted => OnWorkflowCompletedAsync(),
        StageStarted => EnsureWorkHeartbeatAsync(),
        StageCompleted x => ReleaseStageLocksAsync(x.Stage, "completed"),
        StageFailed x => ReleaseStageLocksAsync(x.Stage, "failed"),
        StageApprovalRequested => DisableWorkHeartbeatAsync(),
        StageApprovalResolved x => OnApprovalResolvedAsync(x),
        TaskCompleted => EnsureWorkHeartbeatAsync(),
        TaskFailed => Task.CompletedTask,
        CheckPassed => EnsureWorkHeartbeatAsync(),
        CheckFailed => Task.CompletedTask,
        CheckPending => EnsureWorkHeartbeatAsync(),
        RepairScheduled => Task.CompletedTask,
    };
```

WorkflowGrain 只响应这些编排事件：

- `WorkflowRunStarted` / `WorkflowRunResumed` / `StageStarted` / `TaskCompleted` / `CheckPassed` / `CheckPending` -> ensure heartbeat.
- `WorkflowRunPaused` / `WorkflowRunStopped` / `WorkflowRunCompleted` / `WorkflowRunFailed` / `StageApprovalRequested` -> disable heartbeat.
- `StageCompleted` / `StageFailed` -> release stage lock, wake next waiting workflow.
- `WorkflowRunCompleted` -> run completion hooks.

方向固定：

```text
domain fact happened -> grain reaction runs
```

不能反过来为了触发某个 side effect 而制造 workflow event。

## Design Steps

后续按以下顺序补齐本文：

1. Event set：定义最小 workflow event 集合。
2. Advance semantics：明确一个 WorkflowRun method 如何产生多个边界事实。
3. Commit boundary：定义 WorkflowGrain 提交和 reaction 顺序。
4. Projection mapping：workflow events 到现有 `workflow_*` rows 的映射。
5. Migration plan：按小步替换现有 scattered side effects。
6. Test strategy：domain tests 和 grain/spec tests 分层。
