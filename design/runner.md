# Runner Aggregate

Runner 聚合的信息结构与自报 status 机制。

> 相关不重叠：调度/对账/report 链路见 [`workflow/scheduling.md`](workflow/scheduling.md)；task 派发见 [`workflow/task-dispatch.md`](workflow/task-dispatch.md)；slots 配置权见 issue #222。

## 分组原则：信息状态

字段按**更新生命周期**分组，不按"谁报的"分组。

| 生命周期 | 触发方 | 变化方式 | 失效条件 |
|---|---|---|---|
| 持久 | 控制平面配置 | 罕见、单字段更新 | 不失效 |
| 事件增量 | agent-job push / report | 增删元素 | runner offline 时清空 |
| 快照替换 | 每次 poll（presence）/ register-unregister（info） | 字段覆盖 | 下次 poll 覆盖 |

## 聚合结构

```text
Runner (aggregate root)

  runnerId: string                              # identity

  slots: number                                 # 持久；控制平面拥有

  lastSeen: Instant                             # 快照替换；poll = 心跳，超时即 offline

  info: RunnerInfo | null                       # register 时填、heartbeat-repair 时刷新；
                                                # unregister 清空

  agentJobWorks: List<RunnerWork>               # 事件增量；agent-job push 台账。
                                                # 无 run 可重渲染，故 push 模型保留于此。
```

**没有 workflow 台账**：对账模型下 workflow work 的真相在 run（store-queryable），不在此聚合。slot 不变量（`|Running assigned to me| ≤ slots`）在 `DispatchService` claim 时从 store 实时判定。

```text
RunnerInfo
  state: "online" | "offline"                   # 由 lastSeen 新鲜度判，不由 runner 字面报
  hostname: string
  buildGitHash: string | null
  capabilities: string[]
  coderModels: string[]
  coderModelVariants: Record<string, string[]>
```

## 为什么没有 workflow work 台账

| 检验 | 结果 |
|---|---|
| 守护 Runner 级不变量？ | 否——slot 不变量在 claim 时从 store 实时判，work 级无不变量 |
| 不可从其他聚合派生？ | 否——`store.Where(Status=Running AND AssignedRunnerId=R)` |
| 行为签名需要它？ | 否——全部行为不以 workflow work 为参数 |

它是 WorkflowRun 视角的反向索引，应留在 run。`agentJobWorks` 保留是因为 agent-job 无 run 可重渲染、不可派生。

## 行为签名

```text
Register(info)                    # 首次接入；填 info，state := online，写 registry
Unregister()                      # state := offline，info := null，清 agentJobWorks；
                                  # closeout：Running workflow + agentJob works → FAILED("runner-lost")
TouchPresence()                   # lastSeen := now；不写 registry（poll IS heartbeat）
HeartbeatRepair(info)             # 刷新 info；写 registry
AssignAgentJob(work)              # agentJobWorks.Add
DequeueAssignedAgentJob()         # 取下一个 pending agent-job work → Running
ReportAgentJobResult(id,work,result) # agentJobWorks.Remove → 直报 AgentJobGrain
Update(slots)                     # slots := new（write-through）
```

每个行为精确命中一个组，不跨组写。

## 运行态读取

`GetRuntimeStateAsync()` 投影 `RunnerRuntimeState`（status + lastSeen + activeWorks）供读模型消费。activeWorks 两路合并：

- workflow active works：store 查 `Running assigned to me`，逐 run 投影 current task/checks
- agent-job active works：取自本聚合 `agentJobWorks` 中 Pending/Running 项

## 传输

- **workflow work**：pull-only。`DispatchService` 每 poll 计算 dispatches，report 直达属主 grain。
- **agent-job work**：push。`AssignAgentJob` 推上 runner，poll 时 `DequeueAssignedAgentJob` 取走。
- **presence**：poll 即心跳（`TouchPresence`）。HTTP `Heartbeat` 降级为 info-repair 通道。
- **info**：register/unregister/heartbeat-repair 时写 registry，不每 poll 写。

## 范围外

- 不改对账/claim/report 链路（见 workflow/scheduling.md）
- 不改 slots 配置权（见 #222）
- 不加 runner 控制动作（drain / pause / enable-disable）
- 不持久化 status 历史
- 显式 reassign 出口由 issue #395 跟踪
