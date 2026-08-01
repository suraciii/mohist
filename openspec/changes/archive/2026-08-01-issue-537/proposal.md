## Why

WorkflowRun 持久化 State 单条平均 325 KB、最大 3.6 MB（全表 364 行共 118 MB），超出设计 spec 预算（数百 KB 常态、1 MB 上限）一个数量级。约 81% 的体积来自按 attempt 重复内嵌的 dispatch 快照：每个 TaskRun 的 `DispatchSnapshot` 携带全套 prompts 与全部前序任务输出汇总的全量副本，终态后不清理、重试按份数放大。每次状态变更整体重写、每次读取整体反序列化，叠加 runner 高频上报与 watch 轮询，构成 Server 进程的 LOH 分配风暴（实测 LOH 分配 95%+ 来自 State 反序列化的字符串转码），RSS 峰值达 2 GB。设计 spec（`design/workflow/task-dispatch.md`「Dispatch 快照的持久化」、`design/workflow/run-state.md`「内容边界规则」）已定稿，本 issue 是该 spec 的实装。

## What Changes

- **dispatch 快照不再随 WorkflowRun State 持久化。** 快照与 run State 分离存放；`TaskRun` 不再内嵌 `WorkDispatch`，run State 只保留裁决所需的运行事实。redelivery 路径按需从分离存储单独加载快照。
- **终态即弃。** attempt 进入终态（Completed / Failed / Cancelled）或被后续 attempt 取代后，其快照立即失效、不再保留；不再为已终态 attempt 持有快照。
- **消除按条重复内嵌。** TaskRun 不再携带与其他 TaskRun 重复的全量副本（全套 prompts、全部前序任务输出汇总）。单条 TaskRun 只携带自身裁决所需的字段。
- **redelivery 逐字重放语义不变。** poll redelivery 仍逐字返回首次快照，不重新渲染；仅存储位置与生命周期变化。checks dispatch 继续不持久化快照、走重建（`TranslateToDispatchAsync`），行为不变。
- **升级兼容。** 已在飞行中的活跃 run 升级后，重投递与恢复行为不变。

## Capabilities

- `dispatch-snapshot-persistence`: attempt dispatch 快照的存储位置与生命周期。快照与 WorkflowRun State 分离存放；首次 dispatch 生成（首写胜利）、Running 期间可取且 redelivery 逐字返回；attempt 终态或被取代后立即失效；checks dispatch 不持久化。涵盖从分离存储按需加载的 redelivery 读取路径，以及升级时活跃 run 的快照可用性保持。
- `run-state-content-boundary`: WorkflowRun State 的内容边界。State 只持有裁决所需的最小运行事实（status、assignment、各 Stage/TaskRun 状态机字段、工作区与仓库引用、任务自身 output）；TaskRun 不内嵌 dispatch payload、不携带跨 attempt 重复的全量内容；单条体积回落至预算（数百 KB 常态、1 MB 上限）。

## Impact

- **Server (`packages/server/src/Mohist.Server/`):**
  - `Workflow/Domain/Run/TaskRun.cs:45` — `DispatchSnapshot` 字段移出 TaskRun（不再随 State 序列化）。
  - `Workflow/Grains/WorkflowGrain.cs:481-499` (`StoreActiveWorkDispatchAsync`) — 快照写入改为落分离存储，不再写进 TaskRun。
  - `Runner/Services/DispatchService.cs:242-243, 334-340` — redelivery 快照读取与 `StoreDispatchAsync` 改为读写分离存储；checks 重建路径 (`247-248, 300-301`) 不变。
  - `Workflow/Domain/Run/WorkflowRun.Work.cs:65-81, 216-224` (`CurrentActiveWorkFor` / `ActiveTask`) — 不再从 TaskRun 读取 `DispatchSnapshot`。
  - `Runner/Services/WorkflowItemTranslator.cs:72-84` (`TranslateToDispatchAsync`) — 任务首次渲染路径不变（仍是快照内容来源）。
  - `Infrastructure/Data/Workflow/WorkflowRunStore.cs:132-163` (`StageRunAsync`) — State 序列化不再含快照。
  - `Infrastructure/Data/Workflow/WorkflowRunQuerier.cs:24-35`、`Workflow/Services/WorkflowQuerier.cs:175-184` — 整载读反序列化的 State 不再含快照（体积下降）。
  - 新增：dispatch 快照的分离存储（表 / store），以及 redelivery 按需加载入口。
- **Persistence / Migration:** State 形状变化（`TaskRun.DispatchSnapshot` 不再随 State 落库）—— **BREAKING** 到持久化形状。已嵌入快照的存量 State 行需在冷启动初始化中收敛（外置快照或置空终态快照），沿用 `WorkflowRunStateDataUpgrader` 的 preflight + 单事务 + 幂等模式；活跃 run 的在飞快照必须保持升级后可取。legacy JSON 格式 backfill 由 #536 承担，不在本 issue。
- **Tests:** 现有 dispatch / 重试 / 恢复 / 重投递 spec 与 unit 全绿（安全网）；新增终态即弃、分离存储 redelivery 逐字重放、State 内容边界与体积验收。
