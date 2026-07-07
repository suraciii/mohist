---
purpose: "Define the Runner aggregate's information structure and the self-reported status mechanism."
include:
  - "Aggregate field grouping by information-state lifecycle."
  - "RunnerStatus payload structure pushed via heartbeat."
  - "Behavior signatures and which group each one writes."
  - "Why work-level in-flight tracking is not on this aggregate."
exclude:
  - "Grain-interface call sequences; see workflow/scheduling.md."
  - "Task dispatch protocol; see workflow/task-dispatch.md."
  - "Slot configuration authority and persistence; see issue #222."
  - "HTTP API payloads and Web UI surfaces; #214 covers the read-side projections."
style:
  - "Group state by update lifecycle, not by who reported it."
  - "Prefer code blocks over prose."
  - "Behaviors must not cross information-state groups."
---

# Runner Aggregate

Runner 聚合的信息结构与自报 status 机制。

> 相关不重叠：调度/对账/report 链路见 [`workflow/scheduling.md`](workflow/scheduling.md)；task 派发见 [`workflow/task-dispatch.md`](workflow/task-dispatch.md)；slots 配置权上收与持久化见 issue #222；#214 的详情页只读消费见其 issue。

## 问题

今天 Runner 在干什么，控制平面是看不见的——runner 实际在跑哪些 work、开着哪些 agent session，需要一个上报通道。在对账模型下，这个问题分两层回答：

- **workflow work 的运行真相**由 run 直接持有（run 即台账），server 从 store 查询 `Running assigned to runner X` 即得 desired 集合，无需 runner 聚合再存一份。
- **agent-job work**（push 派发、无 run 可重渲染）与 **presence/capacity** 才属于这个聚合。

## 分组原则：信息状态

字段按**更新生命周期**分组，不按"谁报的"分组（`state: online/offline` 根本不是 runner 字面报的，"自报"这个划分自己就站不住）。

| 生命周期 | 谁触发 | 怎么变 | 失效条件 |
|---|---|---|---|
| 持久 | 控制平面配置 | 罕见、单字段更新 | 不失效 |
| 事件增量 | agent-job push 派发 / report | 增删元素 | runner offline 时清空 |
| 快照替换 | 每次 poll（presence）/ register-unregister（info） | 字段更新 | 下次 poll 覆盖 |

`runnerId` 是身份，不参与任何变化，单独算。

## 聚合结构

```text
Runner (aggregate root)

  runnerId: string                              # identity

  slots: number                                 # 持久；控制平面拥有（#222）

  lastSeen: Instant                             # 快照替换；poll IS heartbeat —— 每次
                                                # poll 的 TouchPresenceAsync 刷新，presence
                                                # 超时即判 offline。无 per-poll registry 写。

  info: RunnerInfo | null                       # register 时填、heartbeat-repair 时刷；
                                                # unregister 清空。控制平面接入事实。

  agentJobWorks: List<RunnerWork>               # 事件增量；agent-job push 台账。无 run 可
                                                # 重渲染，故 push 模型保留于此。
```

注意：**没有 workflow 台账**。对账模型下 workflow work 的真相在 run（store-queryable），这个聚合不再持有任何 workflow 记录——既无 `assignedWorkflows` slot 台账，也无 dispatch/work snapshot。slot 不变量（`|Running assigned to me| ≤ slots`）在 `DispatchService` claim 时从 store 实时查询判定，不靠本聚合维护。

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

按信息状态原则审视，work 级"派出去未报回"的追踪**不属于这个聚合**：

| 检验 | 结果 |
|---|---|
| 守护 Runner 级不变量？ | 否——slot 不变量在 `DispatchService` claim 时从 store 实时判，work 级无不变量 |
| 不可从其他聚合派生？ | 否——"runner R 正在跑哪些 workflow work" = store 查 `Status=Running AND AssignedRunnerId=R` 的每个 run 的 current work |
| 行为签名需要它？ | 否——`Register / Unregister / TouchPresence / AssignAgentJob / DequeueAssignedAgentJob / ReportAgentJobResult / Update` 都不以 workflow work 为参数 |

它本是 WorkflowRun 视角的反向索引。留在 run（work 的拥有者），不进 Runner 聚合。`agentJobWorks` 留着是因为 agent-job 无 run 可重渲染、push 模型需要台账——不可派生、不可省。

## 行为签名

```text
Register(info)                     # 首次接入；填 info，state := online，写 registry
Unregister()                       # state := offline，info := null，清 agentJobWorks，
                                   # closeout: 该 runner 的 Running workflow works（store 查）
                                   # 与 agentJobWorks 合成 FAILED("runner-lost") 直报属主
TouchPresence()                    # lastSeen := now；不写 registry（poll IS heartbeat）
HeartbeatRepair(info)              # 刷新 info（capabilities/models/buildGitHash）；写 registry
AssignAgentJob(work)               # agentJobWorks.Add；push 派发入口
DequeueAssignedAgentJob()          # 取下一个 pending agent-job work；Running 化
ReportAgentJobResult(id,work,result) # agentJobWorks.Remove；直报 AgentJobGrain
Update(slots)                      # slots := new（write-through 持久化）
```

每个行为精确命中一个组，没有任何行为跨组写——这是分组正确的反证。

`TouchPresence` 取代了旧的 per-poll registry 写 + HTTP heartbeat 双通道：poll 即心跳，registry 仅在 register/unregister/heartbeat-repair 时写变更。closeout 在 presence 超时触发，合成 FAILED 走正常 report 通道直达属主（workflow 经 `WorkflowReportService`，agent-job 经本聚合）。

## 运行态读取

`GetRuntimeStateAsync()` 投影出只读的 `RunnerRuntimeState`（status + lastSeen + activeWorks）供读模型（`RunnerStatusService`）消费。activeWorks 由两路合并：

- workflow active works：从 store 查 `Running assigned to me`，逐 run 投影 current task/checks（issue 元数据取自 run annotations）
- agent-job active works：取自本聚合 `agentJobWorks` 中 Pending/Running 的项

## 传输

**Pull via poll**（对账模型），辅以 **push via heartbeat**（info 刷新）。

- workflow work：pull-only。`DispatchService` 每 poll 计算 dispatches，runner 执行后 report 直达属主 grain。无 push。
- agent-job work：push。`AssignAgentJob` 将 work 推上 runner，poll 时 `DequeueAssignedAgentJob` 取走。
- presence：poll 即心跳（`TouchPresence`）。HTTP `Heartbeat` 降级为 info-repair 通道（`HeartbeatRepair`），不参与 presence 判定。
- info：register/unregister/heartbeat-repair 时写 registry，**不每 poll 写**。

## 范围外

- 不改对账/claim/report 链路（见 `workflow/scheduling.md`）
- 不改 slots 配置权归属（见 #222）
- 不替换 #214 的派发台账视图——它在 UI 旁作为"desired/assigned 列"保留，本聚合的 runtime state 是并排的"observed 列"
- 不加 runner 控制动作（drain / pause / enable-disable）
- 不持久化 status 历史（只保留最近一次快照；要历史另立 issue）
- 显式 reassign 出口由 issue #395 单独跟踪
