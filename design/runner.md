# Runner 与调度

Level-triggered reconciliation：调度器不保存记忆，每个决策都是对持久化状态的
无状态查询，由下一次 poll 对账修正。

每个事实只有一个所有者：

```
谁被派发了什么工作        → WorkflowRun / AgentJob（store 可查询）
此刻正在执行什么          → runner 进程内存（每次 poll 上报）
runner 是否存活           → RunnerGrain.lastSeen
```

没有第三份副本。dispatch 永远可以从持久化的 run 重新渲染。

不变量：

```
workflow run 本身就是 dispatch ledger
Running ⟹ 一个 poll 内完成对账：reported ∨ re-dispatched ∨ closed out
|assigned 给 runner 的 Running works| ≤ slots（claim 时检查）
```

## Runner 聚合与 presence

字段按更新生命周期分组，绝不按"谁上报的"分组：

| 生命周期 | 触发 | 变化 | 失效时机 |
|---|---|---|---|
| persistent | 控制平面 | 少量单字段 | 从不 |
| event-increment | agent-job push/report | add/remove | runner offline |
| snapshot-replace | register / 成功 poll / unregister | 整体覆盖 | 下一次成功 poll |

```
Runner
  runnerId                       身份
  slots                          persistent；控制平面拥有
  lastSeen                       snapshot；register 建立，成功 poll 续期
  info: RunnerInfo|null          register 填充；heartbeat-repair 刷新；unregister 清空
  agentJobWorks: [RunnerWork]    event-increment；agent job 的 push ledger（没有 run 可重渲染）

RunnerInfo
  state: online|offline          register 建立；成功 poll 的新鲜度维持
  hostname, buildGitHash
  capabilities, coderModels, coderModelVariants
```

不设 workflow work ledger。workflow 工作的真相在 run 里
（store：`WHERE Status=Running AND AssignedRunnerId=R`）。slot 不变量
（`|running| ≤ slots`）在 claim 时对 store 检查，不在此维护：

| 判定 | 结果 |
|---|---|
| 守护 Runner 自身不变量？ | 否 |
| 无法从其他 aggregate 推导？ | 可推导：`store.Where(Running, AssignedRunnerId=R)` |
| 行为签名需要它？ | 没有行为以 workflow work 为参数 |

### 行为

```
Register(info)                  state=online, lastSeen=now, 填充 info, 写 registry
Unregister()                    state=offline, 清空 info 与 agentJobWorks,
                                closeout → 向 owner 报告 FAILED("runner-lost")
TouchPresence()                 成功 poll：lastSeen=now；恢复 registry 的 online 状态
HeartbeatRepair(info)           只刷新 info；绝不刷新 presence
AssignAgentJob(work)            agentJobWorks.add
DequeueAssignedAgentJob()       下一个 pending → Running
ReportAgentJobResult(id,w,r)    agentJobWorks.remove → AgentJobGrain
Update(slots)                   write-through
```

每个行为只触碰一个分组。

### 运行时读取

`GetRuntimeStateAsync()` → `RunnerRuntimeState`（status + lastSeen + activeWorks）。

activeWorks 合并自：

- workflow：store 查询 `Running assigned to me`，每个 run 的当前 task/checks；
- agent-job：本聚合的 `agentJobWorks`（Pending/Running）。

## 调度协议：claim / pull / report

```
WorkflowGrain / WorkflowRun          ★ 唯一 dispatch ledger
  拥有 Assignment 与工作生命周期（Pending/Running/terminal）
  ClaimNext：原子 Pending→Running + stage lock
  消费 report；幂等（终态工作再报 → Stale）
  无 timer，无 runner 概念

AgentJobGrain
  拥有工作状态 + DispatchSnapshot（没有 run 可供重渲染）

RunnerGrain
  presence、slots、closeout（见上节）
  不持有任何 work 记录

DispatchService（无状态，不是 grain）
  每次 poll：desired − reported → dispatches
  全部来自持久化状态；无 cursor、无 cache、无 ledger

runner 进程（物理）
  一个进程级关键对账循环，拥有 polling + report retry
  并发执行工作；progress-aware timeout
  每次 poll 上报完整状态：inFlight + awaitingAck
  到期 report 按固定间隔重试直至被 ack
```

### 传输方式

| 内容 | 方式 |
|---|---|
| workflow 工作 | pull-only；DispatchService 按 poll 计算；report 直达 owner grain |
| agent-job 工作 | push；AssignAgentJob 放入，poll 时 dequeue |
| presence | poll 即 heartbeat（TouchPresence） |
| info | register/unregister/heartbeat-repair；绝不随 poll |

### Poll 对账

```
runner 进程                     DispatchService                      store/grains
    | POST poll {inFlight, awaitingAck}                                  |
    |------------------------------>|                                    |
    |                               | ⓪ BeginPoll：捕获 slots + gate     |
    |                               | ① TouchPresence（poll=heartbeat）  |
    |                               | ② desired ← Running WHERE assigned=me
    |                               | ③ redelivery = desired − reported  |
    |                               |    每项从持久化 run 重新渲染        |
    |                               | ④ spare = slots − |desired|        |
    |                               |    while spare > 0:                |
    |                               |      assigned 给我的 Ready runs    |
    |                               |      ORDER BY ReadySince ASC       |
    |                               |      ClaimNextAsync ---------------->| Pending→Running
    |                               |        ok → 渲染, spare--          |   + stage lock
    |                               |        null → 下一个候选           |
    |                               |    仍有 spare：可 claim 的 Pending |
    |                               |      → AssignWorker → ClaimNext    |
    | { dispatches[] }              |                                    |
    |<------------------------------|                                    |
    |                               | EndPoll：释放 gate                 |
    | inFlight.add(dispatches)      |                                    |
    | 并发执行                       |                                    |
```

顺序：redelivery 优先（先还欠账）→ assigned 的 Ready runs → claim 新工作。
先保住手上的，再扩张。

`reported − desired`（run 已越过该工作停止）：不采取动作。进程执行到完成，
report 得到 `Stale` 应答 = ack，结果丢弃。

免竞态：进程在收到 dispatch 与下一次 poll 之间同步把工作加进 inFlight。
刚送达的 dispatch 绝不会被误判为丢失。

### Claim

`ClaimNextAsync`：取下一个 pending 工作，获取 stage lock，以 runner 身份标记
Running，持久化。一次原子写。无 offer 阶段，无 runner 侧预注册。

```
PENDING --ClaimNext--> RUNNING --report(success|fail)--> COMPLETED|FAILED
```

claim 失败（stage lock 竞争、状态已变）→ null → 本次 poll 的下一个候选。
claim 成功但 dispatch 丢失 → 工作处于 Running 且未上报 → 下一次 poll 重投。

### 公平性

工作（重新）进入 Ready 时打 `ReadySince` 时间戳。按 `ORDER BY ReadySince ASC`
服务 = 零调度器状态的 round-robin：

```
工作完成 → run 推进 → 下一个工作 pending → ReadySince := now
刚被服务的 run 排到队尾；等最久的在队头
```

可插拔策略点：默认纯 FIFO，可扩展为 `Priority DESC, ReadySince ASC`。

### 容量

`slots` 约束一个 runner 拥有的所有并发执行工作。`BeginPoll` 防止 poll 重叠，
但其容量快照只是参考。每次新的 workflow claim 都在 runner lifecycle gate 下
重新检查 runner 的实时注册与容量。容量下调只约束后续 claim，不取消已在执行的
工作；排在 claim 之前的 unregister 拒绝该 claim，排在 claim 之后的 unregister
将其 closeout。poll 被接纳期间 Agent admission 被拒绝。进程不执行任何容量约束。

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
| poll 传输不可用 | runner 进程 | 有界尝试 → 在同一对账循环内重试 |
| 对账循环意外退出 | runner 进程 | 终止进程 → 服务 supervisor 重启 |
| 工作卡死/失控 | runner 进程 | progress-aware timeout → kill，报告 FAILED |
| runner 消失 | RunnerGrain | poll 新鲜度过期 → offline → closeout：为 Running works 合成 FAILED("runner-lost") |
| 工作超时 | 无 | 上报中的 in-flight 工作视为存活；只有进程判断快慢 |

Register 建立初始 presence，并持久记录最后一次注册档案。HTTP heartbeat 只是
info 刷新通道，绝不能刷新 presence。activation 丢失后，第一次成功 poll 用该
持久档案恢复 presence 与 registry，不需要额外 heartbeat。显式 unregister 清除
持久档案。registry 只在 state/info 变化时写入，绝不随 poll 写。

`runner-lost` 是失败原因，不是 WorkflowRun 状态。owner 把受影响工作记为失败，
WorkflowRun 进入既有 `Failed` 状态，Issue 投影为 `blocked`。没有 `Interrupted`
WorkflowRun 状态。

### 失败处理

| 失败 | 处理 |
|---|---|
| poll 传输失败 | 同一 runner 进程重试；reported set 存续 |
| dispatch 响应丢失 | 下一次 poll：desired − reported → 重投 |
| 进程重启 | 空 report → 全量重投 |
| claim 后渲染失败 | 每次 poll 重试 |
| report 传输失败 | awaitingAck 重试；仍在上报中，绝不重投 |
| 重复/迟到 report | owner 幂等 → Stale |
| 工作卡死 | 进程 timeout → FAILED |
| runner 丢失 | closeout 报告 FAILED("runner-lost")；owner 进入 Failed |
| closeout 后 runner 回归 | report 得到 Stale 应答；工作不再是 desired，自然排空 |
| 工作执行中 run 被停止 | 不取消；report 得到 Stale 应答 |

## 进程契约

runner 进程只有一个进程级关键对账循环，拥有 poll 节奏与未 ack report 的有界
重试。传输失败不结束循环；循环意外退出则进程退出，交给服务 supervisor 重启。
辅助的 heartbeat 或 SignalR 循环绝不能让一个不 poll 的 runner 进程活着。

reported set（`inFlight ∪ awaitingAck`）属于进程生命周期，必须在 poll 异常与
连接恢复后存续——否则一次瞬时 poll 失败 = 手上所有工作从 report 中消失 =
重投风暴。report 重试是由同一循环调度的有界操作，不是独立生命周期的循环。

随 runner 一起丢失的工作以 `FAILED("runner-lost")` 报告给其 owner，由 owner
决定 WorkflowRun 迁移；Runner 没有 `Interrupted` workflow 状态。
