# 聚合协作

参与者：Issue 上下文中的 `Issue`、`Epic`，Workflow 上下文中的 `WorkflowRun`，以及
`Runner`、`Session`。

约定：实线 `→` 表示同步命令；`[Event]` 表示提交后由 durable handler 触发的异步反应。
每一条实线只进入一个目标聚合事务，不表示调用方与目标共享事务。

## 写入权威

| 业务事实 | 唯一写入权威 | 其他参与者如何使用 |
|---|---|---|
| Issue 当前属于哪个 Epic | Issue.`EpicNumber?` | Epic 查询 Issue；WorkflowRun 保存最小运行上下文 |
| Epic 生命周期与推进策略 | Epic | Issue 只携带 `EpicNumber?`，不复制 Epic 状态 |
| Issue 生命周期与当前 WorkflowRun | Issue | Epic 查询；WorkflowRun 结果通过事件返回 |
| Workflow 执行状态 | WorkflowRun | Issue 只保存当前 `WorkflowRunId` |
| Runner presence / capacity | Runner | Workflow 调度只消费其公开事实 |
| Session 生命周期 | Session | WorkflowRun 与 Agent 只保存关联身份 |

不存在独立 membership 聚合、通用 `OwnerRef` 或 controller aggregate。成员列表、进度、
下一个候选 Issue 都是对 Issue 当前状态的查询，不是 Epic 可以独立修改的第二份事实。

## 关联与迁移

```text
User / API → Epic.LinkIssue(issueNumber)
             Epic 读取 Issue 当前归属
             Epic 对新关联校验自身不是 closed
             Epic → Issue.AssignEpic(epicNumber)
                        │
                        └─ transaction: Issue state + [IssueEpicChanged]

[IssueEpicChanged]
  ├→ old Epic.Recompute
  ├→ new Epic.Recompute
  └→ active WorkflowRun.UpdateIssueContext(current Issue context)
```

`LinkIssue` 先读取 Issue 当前归属：已经属于该 Epic 时直接返回成功，即使原请求提交后
Epic 才变为 `closed`，重试也不会把成功结果改成失败；尚未关联时才检查 `closed` 并发送
写命令。

`AssignEpic` 在 Issue 的一个事务里把 `EpicNumber?` 从旧值改为新值，因此移动 Epic 不需要
先 unlink 再 link，也不存在两个 Epic 同时拥有该 Issue 的中间状态。重复分配同一个编号
是 no-op。取消关联使用 `Issue.RemoveEpic(expectedEpicNumber)`；Issue 已经迁移到其他 Epic
时，旧 Epic 的迟到命令不能清掉新归属。

Epic 的校验与 Issue 的归属提交不在同一事务。若 Issue 已提交而调用结果丢失，重试
`LinkIssue` 会命中相同的幂等结果；若 Epic 随后的状态保存失败，`IssueEpicChanged` 的持久
反应仍会让 Epic 重新计算。`done` Epic 因 open Issue 加入而恢复、旧 Epic 因成员离开而
更新进度，都由 `Recompute` 收敛。

handler 不把旧事件 payload 当作当前归属。它先读取 Issue 的当前状态，再向 Epic 或
WorkflowRun 发送完整命令；因此事件乱序或重投不会把旧 Epic 编号写回来。

## Epic 推进 Issue

```text
User → Epic.Start
          │
          └─ transaction: Epic state + [EpicStarted]

[EpicStarted] → Epic.Advance
Epic.Advance → query current Issues → candidate Issue.TryStartFromEpic(epicNumber)
                                           │
                                           └─ transaction: Issue state
                                              + [IssueWorkStarted]

[IssueWorkStarted] → WorkflowRun.EnsureStarted(
                       workflowRunId,
                       { ProjectId, IssueNumber, EpicNumber? })
                         │
                         └─ transaction: WorkflowRun state + WorkflowRun events
```

Epic 的候选查询允许过期；`TryStartFromEpic` 必须在 Issue 内重新检查当前
`EpicNumber`、状态、依赖和是否已有 WorkflowRun。候选已失效时，Issue 拒绝或返回 no-op，
Epic 稍后重新选择。正确性不依赖查询与命令之间的原子性。

Issue 在启动事务中预先分配并保存 `WorkflowRunId`，但不写 WorkflowRun。持久的
`IssueWorkStarted` handler 重新读取 Issue；只有事件仍对应当前 active run 时才调用
`EnsureStarted`。WorkflowRun 以 `WorkflowRunId` 幂等创建并直接进入正常生命周期，不需要
`AwaitingBinding`、`WorkflowBindingPending` 或 lineage revision。创建前、创建后回包前、
handler 确认前的失败都由同一事件重投恢复。

## Workflow 结果与继续推进

```text
Runner → Report → WorkflowRun
                  ├─ transaction: WorkflowRun state + [WorkflowRunCompleted]
                  └─ transaction: WorkflowRun state + [WorkflowRunFailed]

[WorkflowRunCompleted] → Issue.Complete(expectedWorkflowRunId)
[WorkflowRunFailed]    → Issue.AbortWork(expectedWorkflowRunId)

User → Issue.MarkDone
         ├─ require leaf Issue in InProgress
         ├─ require bound WorkflowRun status Stopped or Completed
         └─ transaction: Issue state + [IssueCompleted(completionKind=manual)]

[IssueCompleted / IssueCancelled]
  ├→ Parent Issue.RecomputeComposite       (sub-issue only)
  └→ current Epic.Advance                  (when affiliated)
```

Issue 用 `expectedWorkflowRunId` 拒绝旧 run 的迟到结果。Epic 的下一次推进只由 Issue 已提交
的终态事件触发，不从 WorkflowRun 直接修改 Epic。

手工完成是 Issue 自己的显式生命周期命令，不伪造 `WorkflowRunCompleted`，也不修改
WorkflowRun。IssueGrain 在提交前读取当前绑定 run 的状态；只有 `Stopped` / `Completed`
这两个不可再调度的状态可以通过，`Failed` 仍可 retry，必须由用户先显式 stop。由于允许
值都是 terminal，读取后不会与 resume/retry 竞态。Issue 聚合再次校验自身仍为
`InProgress` 且仍绑定同一个 run，然后写入唯一的 `IssueCompleted` 事实；事件的
`completionKind` 区分 `workflow` 与 `manual`，下游的父 Issue、Epic、Inbox 和指标继续
消费同一种完成事件。

重复命令命中 `Done` 时是 no-op，因此调用结果丢失后的重投不会产生第二条完成事件。
有子 Issue 的父 Issue 不接受手工完成，它的终态只由子 Issue 新鲜快照汇总。

## 同步方向与异步闭环

同一上下文中的 Aggregate 可以双向依赖，但一次同步调用栈不能形成环：

- 关联命令：Epic → Issue；Issue 不在该调用中同步回调 Epic。
- 推进命令：Epic → Issue；Issue 启动 Workflow 和通知 Epic 都走事件。
- 执行结果：WorkflowRun 发事件，Issue handler 再发命令；WorkflowRun 不同步调用 Issue。
- 归属刷新：Issue 发事件，handler 更新 Epic 与 WorkflowRun；目标聚合不在命令中回调 Issue。

这形成业务上的闭环，但每条调用栈有单一方向，每次提交仍只有一个聚合。

## 其他交互

```text
Issue → Cancel → WorkflowRun
Runner ──[RunnerDisconnected]──→ Session (fails affected sessions)

WorkflowRun: Pause, Resume, Approve, Reject, Retry, Rerun
Issue: MarkDone, Archive, Unarchive, Reopen, Close
Runner: Register, Unregister, Heartbeat
```
