# Runner 与调度

调度无记忆：每个决策都是对持久化状态的无状态查询。runner 的自报只用于发现
需要重投的工作；自报不是权威，权威永远从 store 当前内容重建。

每个事实只有一个所有者：

```
谁被派发了什么工作        → WorkflowRun / AgentJob（各自是自己的 dispatch ledger，store 可查询）
此刻正在执行什么          → runner 进程内存（每次 poll 上报）
runner 是否存活           → RunnerGrain.lastSeen
```

没有第二份副本。任何组件都不暂存属于其他 owner 的 dispatch 或工作状态；需要时
从 owner 的持久化状态重建。这是调度协作不需要 reconcile 的原因：不存在需要
核对的第二份事实。

不变量：

```
每个 WorkflowRun / AgentJob 本身就是自己的 dispatch ledger
Running ⟹ 一个 poll 内完成修正：reported ∨ re-dispatched ∨ rejected as invalid ∨ closed out
|assigned 给 runner 的 Running works| ≤ slots（claim 时检查）
```

## Runner 聚合与 presence

字段按更新生命周期分组，绝不按"谁上报的"分组：

| 生命周期 | 触发 | 变化 | 失效时机 |
|---|---|---|---|
| persistent | 控制平面 | 少量单字段 | 从不 |
| snapshot-replace | register / 成功 poll / unregister | 整体覆盖 | 下一次成功 poll |

```
Runner
  runnerId                       身份
  slots                          persistent；控制平面拥有
  lastSeen                       snapshot；register 建立，成功 poll 续期
  info: RunnerInfo|null          register 填充；heartbeat-repair 刷新；unregister 清空

RunnerInfo
  state: online|offline          register 建立；成功 poll 的新鲜度维持
  hostname, buildGitHash
  capabilities, coderModels, coderModelVariants
```

Runner 不持有任何 work 记录。两类工作的真相都在各自 owner 的 store 里
（workflow：run 的行模型；agent-job：AgentJob 的调度投影），都可直接查询
`Pending/Running WHERE AssignedRunnerId=R`。slot 不变量
（`|running| ≤ slots`）在 claim 时对 store 检查，不在此维护：

| 判定 | 结果 |
|---|---|
| 守护 Runner 自身不变量？ | 否 |
| 无法从其他 aggregate 推导？ | 可推导：两类 owner 的 store 查询 |
| 行为签名需要它？ | 没有行为以 work 记录为参数 |

### 行为

```
Register(info)                  state=online, lastSeen=now, 填充 info, 写 registry
Unregister()                    state=offline, 清空 info；
                                closeout → 向 owner 报告 FAILED("runner-lost")
TouchPresence()                 成功 poll：lastSeen=now；恢复 registry 的 online 状态
HeartbeatRepair(info)           只刷新 info；绝不刷新 presence
Update(slots)                   write-through
```

每个行为只触碰一个分组。

### 运行时读取

`GetRuntimeStateAsync()` → `RunnerRuntimeState`（status + lastSeen + activeWorks）。

activeWorks 合并自两类 owner 的 store 直读：

- workflow：`Running assigned to me`，每个 run 的当前 task/checks；
- agent-job：AgentJob 调度投影的 `Pending/Running assigned to me`。

## 调度协议：claim / pull / report

```
WorkflowGrain / WorkflowRun          ★ workflow 工作的 dispatch ledger
  拥有 Assignment 与工作生命周期（Pending/Running/terminal）
  ClaimNext：原子 Pending→Running + stage lock
  消费 report；幂等（终态工作再报 → Stale）
  无 timer，无 runner 概念

AgentJobGrain / AgentJob             ★ agent-job 工作的 dispatch ledger
  拥有工作状态与唯一的 DispatchSnapshot
  admission：eligibility 预检选 runner，单事务写入
    AssignedRunnerId + ReadySince + DispatchSnapshot；不调用其他组件
  ClaimNext：原子 Pending→Running；幂等
  消费 report；幂等（终态工作再报 → Stale）
  reminder 只做一件事：ReadySince 过老仍 Pending → Failed(RunnerUnavailable)

RunnerGrain
  presence、slots、closeout（见上节）
  不持有任何 work 记录

DispatchService（无状态，不是 grain）
  每次 poll：desired − reported → dispatches
  全部来自持久化状态；无 cursor、无 cache、无 ledger

runner 进程（物理）
  一个进程级关键循环，拥有 polling + report retry
  并发执行工作；progress-aware timeout
  每次 poll 上报完整状态：inFlight + awaitingAck
  到期 report 按固定间隔重试直至被 ack
```

### 传输方式

| 内容 | 方式 |
|---|---|
| 所有工作（workflow 与 agent-job） | pull-only；DispatchService 按 poll 计算；report 直达 owner grain |
| presence | poll 即 heartbeat（TouchPresence） |
| info | register/unregister/heartbeat-repair；绝不随 poll |

### Poll 计算

```
runner 进程                     DispatchService                      store/grains
    | POST poll {inFlight, awaitingAck}                                  |
    |------------------------------>|                                    |
    |                               | ⓪ BeginPoll：捕获 slots + gate     |
    |                               | ① TouchPresence（poll=heartbeat）  |
    |                               | ② desired ← 两类 owner 的           |
    |                               |    Running assigned=me              |
    |                               | ③ redelivery = desired − reported   |
    |                               |    每项从 owner 的持久状态重建       |
    |                               | ④ spare = slots − |active works|    |
    |                               |    while spare > 0:                 |
    |                               |      Pending assigned=me，再         |
    |                               |      可 claim 的 Pending            |
    |                               |      各自 ORDER BY ReadySince ASC   |
    |                               |      ClaimNext ------------------->| Pending→Running
    |                               |        ok → 构造 dispatch, spare--  |   (+ stage lock)
    |                               |        null → 下一个候选            |
    | { dispatches[] }              |                                    |
    |<------------------------------|                                    |
    |                               | EndPoll：释放 gate                 |
    | inFlight.add(dispatches)      |                                    |
    | 并发执行                       |                                    |
```

顺序：redelivery 优先（先还欠账）→ assigned 给本 runner 的 Pending → 可 claim
的 Pending。先保住手上的，再扩张。

`reported − desired`（owner 已越过该工作停止）：不采取动作。进程执行到完成，
report 得到 `Stale` 应答 = ack，结果丢弃。

免竞态：进程在收到 dispatch 与下一次 poll 之间同步把工作加进 inFlight。
刚送达的 dispatch 绝不会被误判为丢失。

### Claim

`ClaimNextAsync`：取下一个 pending 工作（workflow 带 stage lock），以 runner
身份标记 Running，持久化。一次原子写。无 offer 阶段，无 runner 侧预注册。

```
PENDING --ClaimNext--> RUNNING --report(success|fail)--> COMPLETED|FAILED
```

claim 失败（stage lock 竞争、状态已变）→ null → 本次 poll 的下一个候选。
claim 成功但 dispatch 丢失 → 工作处于 Running 且未上报 → 下一次 poll 重投。

dispatch 构造失败分两类。外部依赖或可变配置导致的普通失败保留 Running，由下一次
poll 重试；持久 WorkItem 的 `uses` 命中退役 Action 时，translator 返回明确的不可重试
拒绝，DispatchService 用 `workerId + workId` 命令 owner 将该工作记为 Failed。该命令
必须核对当前 active work，不能用“失败当前任务”误伤已经推进后的新工作。Action 输入契约
（未知键、缺 required、类型错）由 Runner 在渲染后 manifest 校验阶段判定，按
[`actions.md`](workflow/actions.md) 与 [`task-dispatch.md`](workflow/task-dispatch.md)
执行，不归 dispatcher 处理。

### 公平性

工作（重新）进入 Ready 时打 `ReadySince` 时间戳。同一候选层级内 workflow 与
agent-job 按 `ORDER BY ReadySince ASC` 混排服务 = 零调度器状态的 round-robin：

```
工作完成 → owner 推进 → 下一个工作 pending → ReadySince := now
刚被服务的排到队尾；等最久的在队头
```

可插拔策略点：默认纯 FIFO。交互触发的 agent-job 如需优先于后台 workflow，
扩展为 `Priority DESC, ReadySince ASC`——优先级必须是显式策略，不做隐式偏袒。

### 容量

`slots` 约束一个 runner 拥有的所有并发执行工作（workflow 与 agent-job 合计）。
容量裁定只有一处：每次新的 claim 都在 runner lifecycle gate 下复查 runner 的
实时注册与容量。`BeginPoll` 防止 poll 重叠，但其容量快照只是参考。容量下调只
约束后续 claim，不取消已在执行的工作；排在 claim 之前的 unregister 拒绝该
claim，排在 claim 之后的 unregister 将其 closeout。进程不执行任何容量约束。

AgentJob admission 的容量检查是预检：选 runner 时以 eligibility 过滤实时容量
已满者，全部已满则同步拒绝——调用方立即看到背压。预检通过不承诺容量；终审
在 claim。预检到终审之间容量可能被其他工作占用，此时 job 留在 Pending，由
下一次 poll 再审。容量的同步承诺本来就无法兑现（任何裁定与真正执行之间都
存在窗口），两段式把承诺收窄到能兑现的范围。

### Report

report 直达 owning grain。翻译服务无状态，不设中继：

```
runner → api route → 翻译（无状态） → owner grain → Accepted | Stale（都是 ack）
```

At-least-once：完成的工作 → `awaitingAck` → 按固定间隔重试原始结果 → 仍在
poll report 中 → 绝不被误判为丢失。`Accepted` 与 `Stale` 都终止重试。

report 的产生者对 owner 不可区分：执行进程（正常或 timeout 失败）或
RunnerGrain closeout。

## Supervision 与 runner-lost closeout

| 情形 | 负责者 | 处理 |
|---|---|---|
| poll 传输不可用 | runner 进程 | 有界尝试 → 在同一循环内重试 |
| 循环意外退出 | runner 进程 | 终止进程 → 服务 supervisor 重启 |
| 工作卡死/失控 | runner 进程 | progress-aware timeout → kill，报告 FAILED |
| runner 消失 | RunnerGrain | poll 新鲜度过期 → offline → closeout：对两类 owner 查询 `Running assigned=me`，逐个向 owner 报告 FAILED("runner-lost") |
| 工作超时 | 无 server 侧计时 | 上报中的 in-flight 工作视为存活；只有进程判断快慢。owner 自有计时（AgentJob 的执行超时与 dispatch 超时）由各自 reminder 裁定，与调度无关 |

Register 建立初始 presence，并持久记录最后一次注册档案。HTTP heartbeat 只是
info 刷新通道，绝不能刷新 presence。activation 丢失后，第一次成功 poll 用该
持久档案恢复 presence 与 registry，不需要额外 heartbeat。显式 unregister 清除
持久档案。registry 只在 state/info 变化时写入，绝不随 poll 写。

`runner-lost` 是失败原因，不是 owner 状态。owner 把受影响工作记为失败：
WorkflowRun 进入既有 `Failed` 状态，Issue 投影为 `blocked`；AgentJob 对称地
进入既有 `Failed` 状态。没有 `Interrupted` 状态。

### 失败处理

| 失败 | 处理 |
|---|---|
| poll 传输失败 | 同一 runner 进程重试；reported set 存续 |
| dispatch 响应丢失 | 下一次 poll：desired − reported → 重投 |
| 进程重启 | 空 report → 全量重投 |
| claim 后构造 dispatch 发生普通失败 | 保持 Running；每次 poll 重试 |
| 持久 WorkItem 命中退役 Action | 按 `workerId + workId` 拒绝该工作；owner 记为 FAILED |
| Runner 渲染或 manifest 校验失败 | attempt 失败 `invalid-input`，不重投 |
| report 传输失败 | awaitingAck 重试；仍在上报中，绝不重投 |
| 重复/迟到 report | owner 幂等 → Stale |
| 工作卡死 | 进程 timeout → FAILED |
| runner 丢失 | closeout 报告 FAILED("runner-lost")；owner 进入 Failed |
| closeout 后 runner 回归 | report 得到 Stale 应答；工作不再是 desired，自然排空 |
| 工作执行中 run/job 被停止 | 不取消；report 得到 Stale 应答 |
| agent-job 长期无可用 runner | owner 的 ReadySince 超时 → FAILED(RunnerUnavailable) |

## 进程契约

runner 进程只有一个进程级关键循环，拥有 poll 节奏与未 ack report 的有界
重试。传输失败不结束循环；循环意外退出则进程退出，交给服务 supervisor 重启。
辅助的 heartbeat 或 SignalR 循环绝不能让一个不 poll 的 runner 进程活着。

reported set（`inFlight ∪ awaitingAck`）属于进程生命周期，必须在 poll 异常与
连接恢复后存续——否则一次瞬时 poll 失败 = 手上所有工作从 report 中消失 =
重投风暴。report 重试是由同一循环调度的有界操作，不是独立生命周期的循环。

随 runner 一起丢失的工作以 `FAILED("runner-lost")` 报告给其 owner，由 owner
决定后续；没有 `Interrupted` 状态。

## 本地 workspace 生命周期

Runner 拥有自己物化的 workspace 与本地 `WorkspaceRegistry`。注册表只是可重建的
生命周期索引，不是 WorkflowRun 状态权威。每个条目只有三种 phase：

| phase | 含义 |
|---|---|
| `active` | Runner 尚未观察到 WorkflowRun 的不可恢复终态；禁止自动回收 |
| `eligible` | 已观察到 `Completed` 或 `Stopped`；可以释放 Runtime 资源，并按磁盘策略删除 workspace |
| `stuck` | WorkflowRun 已终结，但磁盘删除安全检查确定性拒绝；保留目录与所有权记录，不再重复尝试自动删除 |

`Failed` 是可由 Retry / Rerun 恢复的中间态，不进入 `eligible`。未知状态同样保持
`active`。终态 push 只优化延迟；Runner 在启动、重连和周期收敛时只向 Server 批量查询
本地仍为 `active` 的 WorkflowRun。它不扫描 Server 的完整 Workflow 历史，也不重新查询
已经进入 `eligible` 或 `stuck` 的条目。

一个 workflow workspace 同时关联两类可回收资源：

- 磁盘 workspace 是 Git worktree，按 retention 与 storage budget 回收；
- Runtime directory resource 是外部 Runtime 为该目录保留的进程内资源，按 Runtime
  自己的安全条件尽快释放。

两者只有目录身份相同，生命周期彼此独立。释放 Runtime directory resource 不删除
worktree；删除 worktree 也不能替代 Runtime 释放。两类回收共用 Runner 已有的 workspace
维护周期，默认每 2 分钟执行一次，不增加按 WorkflowRun 独立运行的 timer 或新的用户配置。
每轮 single-flight：上一轮未结束时不重叠执行下一轮。周期维护先执行 Runtime 释放，再执行
磁盘策略；Runtime 释放不依赖 retention、storage budget 或 Server 配置读取成功。

磁盘删除有额外顺序约束：如果当前 Runtime generation 仍记录该目录有尚未释放的资源，
自动清理必须等到 Runtime 确认释放后才能删除目录和移除注册表条目。手动 workspace
cleanup 也遵循相同顺序；Runtime 确认目录仍忙或无法判断时，本次删除明确失败或延后，
不能先删目录再丢失释放所需的身份。OpenCode 的具体条件见
[`runtimes/opencode.md`](runtimes/opencode.md#directory-instance-回收)。

## 落盘状态

runner 在 `<runnerRoot>/.mohist/runner-state/` 下持久化四类状态，全部原子写
（临时文件 + rename），启动时载入。四类状态的损坏语义不同，由「丢了能不能
重建」决定：

| 文件 | 内容 | 损坏语义 |
|---|---|---|
| `runtime-events.json` | 运行时事件 outbox：待投递到 server 的 session 事件队列；每产生一个事实即快照写入 | 不可读 → outbox 不健康并按本地重试节奏重载，绝不改写不可读文件；超出保留上限时最先丢弃可重建的流式增量 |
| `followup-operations.json` | followup 操作幂等日志：operationId → claimed / submitted；状态迁移即写入 | 版本或形状不符 → 日志不可用并拒绝新操作（fail-closed）；文件不存在视为全新开始 |
| `session-commands.json` | session 命令幂等日志：operationId → started / completed + result；同上 | 同上：损坏 fail-closed，缺失视为全新开始 |
| `workspaces.json` | runner 本地 workspace 注册表：已物化 worktree 及其身份；物化与终态迁移即写入，active 条目存续到观察到终态 | 不可读或损坏 → 当作空表启动（fail-open），下次写入时重建——磁盘上的 worktree 才是事实，注册表只是索引 |

幂等日志 fail-closed 是因为丢了就可能重复执行；注册表 fail-open 是因为它能
从磁盘重建。四类状态都是 runner 私有：server 从不直接读写，跨进程一致性靠
事件投递与 poll 重算，不靠共享文件。

## 决策记录：单一 ledger，无 reconcile

agent-job 工作曾经经 push 通道投递：AgentJobGrain 把 DispatchSnapshot 跨 grain
推给 Runner 聚合暂存，Runner 侧持久化第二份 work 记录，再靠周期性的
reconcile 核对暂存与 owner 之间的漂移。该形态违反本文开头的不变量（没有
第二份副本）：reconcile 不是设计，而是冗余副本的维持成本——连同它带来的
跨 grain 回调环、assignment 与 poll 的互斥竞态、activation 时的 ledger
hydrate，全都是同一笔赎金。

统一为：AgentJob 与 WorkflowRun 一样是自己的 dispatch ledger。调度所需字段
（Status、AssignedRunnerId、ReadySince、DispatchSnapshot）以可查询投影
持久化，DispatchService 对两类 owner 做同一组 desired 计算，claim 由 owner
原子完成。Runner 聚合回归 presence / slots / closeout，不持有任何 work
记录；assign 回调与 runnable 反查构成的跨 grain 环随之消失；容量裁定收敛到
claim 一处。原 push 通道的 Runner 侧暂存、reconcile 循环与 dispatch 重试
状态机（DispatchAttempts / retry bound / acceptance fence）整体删除；agent-job
长期无可用 runner 的失败语义由 owner 自己的 ReadySince 超时承接。
