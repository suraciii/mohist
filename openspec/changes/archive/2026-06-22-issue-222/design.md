## Context

Runner 子域当前只建模运行态。`slots`（并发容量）由 runner 进程经 `MAX_CONCURRENT_WORKFLOWS` env 决定，通过 register/heartbeat "上报"进 `RunnerGrain` 内存（`RunnerGrain.cs:150` 的 `MaxWorkflowSlots => RunnerCapacity.Normalize(_info?.MaxWorkflowSlots)`），**无持久化**。Orleans 回收 grain 后该值丢失，下次心跳若字段缺失会被 `Normalize` 回落默认值 1——曾导致 capacity 从 4 被重置成 1。

现状关键点：
- `RunnerGrain`（`[Reentrant]`，单 grain per runnerId）混合持有运行态：`_status`、`_info`、`_works`、`_lastHeartbeat`，外加 `_projectId` 决定 registry 路由（`RunnerRegistryKey() => _projectId ?? RunnerRegistryKeys.Global`，`RunnerGrain.cs:384`）。
- slot 不变量有两个执行点：`RunnerGrain.PollAsync`（`RunnerGrain.cs:133`）和 `AgentJobGrain.TryAssignToRunnerAsync`（`AgentJobGrain.cs:244`，读 `runnerInfo.MaxWorkflowSlots`）。
- 持久化范式有两条：EF Core `MohistDbContext` + `IStateStore<T>`（如 `AgentStore`/`AgentRow`，JSON State blob + 计算列）用于控制平面关系数据；Orleans grain storage 用于 grain 状态。
- registry 消费点散布在 `RunnerStatusService.cs:22`、`AgentRoutes.cs:65-66`、`OpencodeRoutes.cs:22/25`、`RunnerIdentityRoutes.cs:18`、`RunnerWorkspaceClient.cs:115`、`AgentJobGrain.cs:215-218`，以及 `ListEligibleRunnersAsync`（`RunnerRegistryGrain.cs:65-94`）做跨 global/project registry 合并。

约束：本地优先单机系统、单操作者；项目正处积极开发期，无需版本兼容。

## Goals / Non-Goals

**Goals:**
- 给 Runner 补建**定义态**：`slots` 持久化、由控制平面配置、派发端消费。
- `slots` 权威来源切换为持久化定义态；runner 上报值不再参与派发。
- Runner **全局化**：去掉 project scope，统一走 global 派发路径，保留 round-robin 跨 project backlog 公平认领。
- 新增 `PATCH` 配置 `slots` 的 API。
- 彻底修掉 capacity 易失的结构性根因。

**Non-Goals:**
- runner 控制动作（drain / pause / enable-disable）—— 后续 issue。
- runner 历史执行记录与成功率统计。
- heartbeat / 健康检查机制改造（保持现状，结果仍通过 `GoOffline()` 进入领域）。
- `MAX_CONCURRENT_WORKFLOWS` env 移除（保留为 runner 本地认知，不影响 server 派发）。
- UI 配置入口实现（由 #214 详情页承载，互补）。

## Decisions

### Decision 1: 定义态用新 EF Core 表持久化，而非 Orleans grain storage

新增 `Runners` 表（`RunnerRow`：`Id`（PK，runnerId）、`Slots`（int）、`CreatedAt`、`UpdatedAt`），通过 `MohistDbContext` 注册 + 一个 EF migration。配一个 `RunnerDefinitionStore`（`IDbContextFactory<MohistDbContext>`）提供 `GetOrInitAsync(runnerId)` / `UpdateSlotsAsync(runnerId, slots)`。

**理由**：定义态是控制平面拥有的小体量、标量、可查询数据；必须独立于易失运行态存活。grain storage 会把运行态（`_works`、`_status`）一起持久化，与"运行态易失"立场冲突，且外部 PATCH 写 grain storage 时序不直接。EF 表匹配现有 `AgentRow` 范式，但用真实列而非 JSON blob——`slots` 是容量契约标量，未来扩展（drain 状态等）直接加列。

**备选**：(a) Orleans grain storage 持久化 RunnerGrain 全状态——拒绝，原因如上；(b) 复用 `RunnerRegistryGrain` 作持久化边界——拒绝，它是 in-memory 查询索引，非定义态归属。

### Decision 2: RunnerGrain 内存缓存 slots，write-through 保一致

`RunnerGrain` 持有一个 `int? _slots` 缓存字段。读取策略：首次访问（`RegisterAsync`）时 `GetOrInitAsync` 加载并缓存；`OnActivateAsync` 重新激活时同样加载。`UpdateAsync(slots)` 为 **write-through**——一次 grain 调用内先落库再更新缓存字段。派发读 `MaxWorkflowSlots` 改读缓存字段（`RunnerGrain.cs:133/150`）。

**理由**：派发是热路径（`PollAsync`），不应每次打 DB。RunnerGrain 按 runnerId 单实例化（一 runner 一 grain），缓存天然单源一致；`UpdateAsync` 经同一 grain，写库与写缓存原子可见。grain 回收后 `OnActivateAsync` 从 DB 重载——这正是修掉 capacity 易失根因的关键。

**备选**：每次派发读 DB——拒绝，热路径开销且 `[Reentrant]` 下无必要；单独配置 grain——拒绝，多一跳无收益。

### Decision 3: slots 单一读取源是 RunnerGrain，而非 registry RunnerInfo

registry 的 `RunnerInfo.MaxWorkflowSlots` 字段**降级为非权威**（保留以维持 runner 线兼容，同 `ProjectId` 处理）。所有需要容量的消费点改为问 grain：
- `AgentJobGrain.TryAssignToRunnerAsync`（`AgentJobGrain.cs:244`）改调 `RunnerGrain` 取 slots，不再读 `runnerInfo.MaxWorkflowSlots`。
- `RunnerStatusService.ProjectRunnerAsync`（`RunnerStatusService.cs:58`）的 capacity view 改读 grain。

新增 `IRunnerGrain.GetSlotsAsync()`（或 `GetDefinitionStateAsync`）最小方法，保持运行态/定义态读取分离。

**理由**：保留单一事实源（grain，背后是 DB）。若让 registry 也存 slots，会重现"双源可能不一致"的老问题。

### Decision 4: 全局化——移除 project scope 路由

`RunnerGrain` 移除 `_projectId` 与 `RunnerRegistryKey()` 分支，registry 路由恒为 `RunnerRegistryKeys.Global`。所有消费点迁移到 Global：
- `RunnerStatusService.cs:22` → Global。
- `AgentRoutes.cs:65-66` 的 project+global 双查 → 单查 Global。
- `OpencodeRoutes.cs:25` 的 project registry → Global。
- `AgentJobGrain.cs:215-218` 的 registryKey 分支 → 恒 Global。
- `RunnerRegistryGrain.ListEligibleRunnersAsync`（`:65-94`）的跨 registry 合并简化为返回 global 全量；`projectId` 过滤语义消失。

`RunnerInfo.ProjectId` 与 `RunnerRegisterRequest.ProjectId`/`RunnerHeartbeatRequest.ProjectId` 字段**保留**（runner 线兼容、非权威），但不再参与路由。

**理由**：issue 明确"去掉 project scope"。`BacklogProjectIdsAsync`（`RunnerGrain.cs:395-413`）的 global 分支已实现跨 project round-robin 公平认领，全局化后该分支成为唯一路径，行为不变。

**备选**：保留 per-project registry 机制"以备将来"——拒绝，会 reintroduce 双源复杂度；将来若需项目绑定，可作为定义态属性重新加回。

### Decision 5: PATCH 端点形状

`RunnerRoutes.cs` 在 `/api/runner/{runnerId}` 组下新增 `MapPatch`，body `{ slots: int }`。流程：校验 `slots` 为正整数（否则 400）→ `RunnerGrain.UpdateAsync(slots)`（write-through）→ 返回更新后的定义态视图（`{ runnerId, slots }`）。

**理由**：PATCH on resource root 是 REST 惯例；body 最小；调用 grain 而非直接写 DB，确保缓存与库一致（与 Decision 2 配套）。

## Risks / Trade-offs

- **[迁移把操作者 env 维持的容量降为 1]** → Mitigation：文档化为一次性影响；#214 UI 详情页提供配置入口；`PATCH` API 可脚本化批量回填。
- **[RunnerGrain 缓存与 DB 不一致]** → Mitigation：所有 slot 写入强制走 `RunnerGrain.UpdateAsync`（单 grain per runner = 一致）；禁止任何旁路 DB 写。代码注释标明此约束。
- **[`[Reentrant]` 下 UpdateAsync 与 PollAsync 并发]** → Mitigation：`slots` 是单 int，.NET 下读写原子；容量判断 `ActiveWorkflowCount >= _slots` 即便读到旧值也只是该轮保守，下一轮纠正。可接受。
- **[全局化移除项目级 runner 绑定]** → Mitigation：本系统为本地优先单操作者，project-scoped runner 非文档化特性；issue 显式列为目标。`RunnerInfo.ProjectId` 字段保留，未来可作定义态属性复用。
- **[`RunnerInfo.MaxWorkflowSlots` 保留为死数据可能误导]** → Mitigation：spec 标记非权威；代码注释说明降级原因；后续 issue 可清理。

## Migration Plan

1. 新增 `RunnerRow` + `MohistDbContext` 注册 + EF migration（`AddRunnerDefinitionTable`）。schema 纯加表，启动时自动迁移。
2. 部署：schema migrate on startup。现有 runner 首次 reconnect → 无行 → `GetOrInitAsync` 初始化 `Slots=1`。操作者按需用 `PATCH /api/runner/{runnerId}` 或 #214 UI 配回原容量（如 4）。
3. **回滚**：`dotnet ef database update <prev>` 回退 migration（删表）+ 代码 revert。回滚后 slots 行为退回 runner 上报的 `MaxWorkflowSlots`。代价：已配置的 slots 丢失（操作者需重建 env 配置）。对本地优先单机系统可接受。
4. 不采纳"用 runner 上报值一次性迁移填充"——符合"配置权完全在控制平面"立场（issue 明示）。

## Open Questions

- `GetSlotsAsync` vs 扩展 `GetRuntimeStateAsync`：倾向新增最小方法，保持运行态/定义态读取分离。待实现时定。
- `RunnerRegistryKeys.ForProject` / `GrainKey.RunnerRegistry(projectId)` 调用点全迁移后，helper 本身是删除还是标 `[Obsolete]`：倾向删除调用点、保留 helper 标 obsolete 一个周期，降低跨 PR 合并冲突风险。
- `RunnerStatusView` 的 capacity 是否需要同时暴露 "configured slots" 与 "active count"：#214 UI 需要展示二者，倾向 capacity view 扩展为 `{active, slots}`（当前已是该形状，仅切换 slots 来源）。
