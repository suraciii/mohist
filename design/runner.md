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

> 相关不重叠：调度/claim/bind/report 链路见 [`workflow/scheduling.md`](workflow/scheduling.md)；task 派发见 [`workflow/task-dispatch.md`](workflow/task-dispatch.md)；slots 配置权上收与持久化见 issue #222；#214 的详情页只读消费见其 issue。

## 问题

今天 Runner 在干什么，控制平面是看不见的：

- `assignedWorkflows`（slot 台账）知道"派出去哪些 workflow run"
- 但 runner 实际在跑哪些 work、开着哪些 agent session，没有上报通道

派发台账与运行真相会 drift——runner 崩了一个 work、僵尸 session、stale claim——控制平面全无感知。#214 的"active work 上下文"是派发记录的投影，不是运行真相。

## 分组原则：信息状态

字段按**更新生命周期**分组，不按"谁报的"分组（`state: online/offline` 根本不是 runner 字面报的，"自报"这个划分自己就站不住）。

| 生命周期 | 谁触发 | 怎么变 | 失效条件 |
|---|---|---|---|
| 持久 | 控制平面配置 | 罕见、单字段更新 | 不失效 |
| 事件增量 | Claim/Release | 增删元素 | runner offline 时清空 |
| 快照替换 | 每次心跳 | 整体覆盖 | 下次心跳覆盖 |

`runnerId` 是身份，不参与任何变化，单独算。

## 聚合结构

```text
Runner (aggregate root)

  runnerId: string                              # identity

  slots: number                                 # 持久；控制平面拥有（#222）

  assignedWorkflows: Set<WorkflowRunRef>        # 事件增量；slot 不变量的载体
                                                # 不变量: |assignedWorkflows| ≤ slots

  status: RunnerStatus                          # 快照替换；每次心跳整体覆盖
```

根上四个字段。前两个是控制平面内部事实，后两个——`assignedWorkflows` 是 server 的租约台账，`status` 是 runner 的当前快照——两者并列、互不蕴含、可对账 drift。

```text
RunnerStatus

  # 连接判定（server 据 reportedAt 新鲜度判；类比 NodeStatus.conditions.Ready）
  state: "online" | "offline"
  reportedAt: Instant

  # 接入事实（register 时填、心跳时刷）
  hostname: string
  buildGitHash: string | null
  capabilities: string[]
  coderModels: string[]
  coderModelVariants: Record<string, string[]>

  # 运行事实（每次心跳整体替换）
  activeWorks: ReportedActiveWork[]
  agentSessions: ReportedAgentSession[]
```

`status` 内部不再分层。原因：心跳 payload 是 runner 一次性吐出的完整快照，人为切 connection / activity 子对象会让 runner 端构造时凭空多一个分界，并且 `GoOffline` 行为要同时影响多层，跨层写违反"行为不跨组"。

```text
ReportedActiveWork

  workflowRunId: string | null                  # null = agent-job（无 workflow 上下文）
  workId: string
  ownerKind: "workflow" | "agent-job"
  agentJobId: string | null

  stage: string | null                          # 与 #214 active work 字段集对齐
  title: string | null
  issueNumber: number | null

  startedAt: Instant                            # runner 从 poll 到该 work 的时刻；只有 runner 知道

ReportedAgentSession

  sessionId: string                             # ACP session id
  workflowRunId: string | null                  # 哪个 workflow run 开的；null = 无归属
  workDir: string                               # session 绑定的工作目录
  model: string | null                          # 该 session 用的 coder model
  openedAt: Instant                             # session 建立时刻
```

## 为什么没有 inFlightWorks

按信息状态原则审视，work 级"派出去未报回"的追踪**不属于这个聚合**：

| 检验 | 结果 |
|---|---|
| 守护 Runner 级不变量？ | 否——slot 不变量在 `assignedWorkflows`，work 级无不变量 |
| 不可从其他聚合派生？ | 否——"派给 R 未报回的 work" = `assignedWorkflows` 中每个 WorkflowRun 的 currentWork 的并集 |
| 行为签名需要它？ | 否——`Register / GoOffline / Claim / Release / Update / ReportStatus` 都不以 work 为参数 |

它本是 WorkflowRun 视角的反向索引。留在 WorkflowRun（work 的拥有者）和 dispatch 基础设施（work→runner 路由索引）那一层，不进 Runner 聚合。

`assignedWorkflows` 留着是因为它守护 slot 不变量、需要 O(1) 计数——不可派生、不可省。

## 行为签名

```text
Register(facts)                  # 首次接入；等价于一次 ReportStatus + state=online
ReportStatus(snapshot)           # 心跳到来；整体覆盖 status 所有字段，state := online
GoOffline()                      # 仅 state := offline；其余 status 字段保留为最后一次心跳的遗容
Claim(ref)                       # assignedWorkflows.Add(ref)
                                 # 前置: |set| < slots 且 state = online
Release(ref)                     # assignedWorkflows.Remove(ref)
Update(slots)                    # slots := new
```

每个行为精确命中一个组，没有任何行为跨组写——这是分组正确的反证。

`ReportStatus` 是本期新增。其余五个已在现有 RunnerGrain 上落地（见 `RunnerGrain.cs`）。

## 传输

**Push via heartbeat**，不开新通道。

- 复用现有 `HeartbeatAsync` 链路；payload 增加 `status` 字段
- runner 端 `RunnerHost.currentStatus()` 收集快照后随心跳推出
- server 端 `RunnerGrain.ReportStatus()` 整体替换 status 字段

类比 K8s：kubelet 定期 POST NodeStatus 到 API server，API server 落到 Node.status；调度器/controller 对比 `.spec` 与 `.status`。Mohist 同款——server 持有最近一次 status，UI/CLI 永远能拿到，即使无人查询也持续对账。

不走 SignalR pull 的原因：pull 适合"运维当下 debug"，不适合作为权威状态通道（runner 离线时无回落、需要新调用面、无法支撑持续对账）。

## runner 侧需要补的采集

要让 `RunnerHost.currentStatus()` 喂得出 `RunnerStatus`，runner 进程要补两块状态：

1. **inFlight 提升**：`runWorkerPool()` 内 `inFlight: Map<string, Promise>` 是局部变量 → 提升为 `RunnerHost.activeWorks: Map<string, { work: WorkItem, startedAt: Instant }>`。poll 进来时 set，`.finally` 里 delete。
2. **`SessionEntry` 补字段**：`acp-connection.ts` 现在的 `{ sessionId, workDir, model? }` → 加 `workflowRunId: string | null` 与 `openedAt: Instant`。`AcpSessionManager.set()` 时填入。

## 范围外

- 不改派发/claim/bind/report 链路（见 `workflow/scheduling.md`）
- 不改 slots 配置权归属（见 #222）
- 不替换 #214 的派发台账视图——它在 UI 旁作为"desired/assigned 列"保留，本期新增的 status 是并排的"observed 列"
- 不加 runner 控制动作（drain / pause / enable-disable）
- 不持久化 status 历史（只保留最近一次快照；要历史另立 issue）
