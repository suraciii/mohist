## Why

Runner 子域当前只建模了运行态，缺定义态。`slots`（并发容量）漂在 runner 进程的环境变量 `MAX_CONCURRENT_WORKFLOWS` 里，通过注册/心跳"上报"进 `RunnerGrain` 内存，**无持久化**。Orleans 回收 grain 后状态重置，下次心跳缺失字段被 `Normalize` 回落默认值——曾导致 capacity 从 4 被重置成 1（心跳带全字段只是临时缓解，结构性根因未除）。同时 runner 带 project scope，server 不是单一事实源，改 slot 数要改 systemd override + 重启 runner。本变更给 Runner 补建定义态：`slots` 由控制平面配置、持久化、派发端消费，并把 Runner 全局化，让"配置权"与"执行端接入"彻底解耦——改了配置，下次派发立即生效，runner 进程零感知。

## What Changes

- **新增 Runner 定义态持久化**：新增 DB 表存 per-runner 定义态（`runnerId` + `slots`，预留扩展位）。runner 接入后配置落库；执行端下线/重启/grain 回收后定义态不丢。
- **切换 `slots` 权威来源**：`RunnerGrain.MaxWorkflowSlots`（`RunnerGrain.cs:150`）改读持久化定义态；新 runner 首次接入时用默认值 1 初始化实体。runner 进程通过 register/heartbeat 上报的并发数**不再作为派发用的 slots 来源**（字段保留为 runner 本地认知，但不影响 server 派发）。
- **新增配置 API**：`PATCH` 接口配置 `slots`，落库后下次派发生效。UI 配置入口与 #214 详情页协同（#222 聚焦后端 + API）。
- **BREAKING**（一次性迁移影响）：切换后，现有 runner 首次接入时 DB 无定义态，`slots` 初始化为默认值 1。原本靠 `MAX_CONCURRENT_WORKFLOWS` env 维持的容量（如 4）需在 UI/API 手动配回。符合"配置权完全在控制平面、执行端上报值不参与"的立场——不采纳 runner 上报值做一次性迁移填充。
- **Runner 全局化**：去掉 project scope，所有 runner 走 global 派发路径（`BacklogProjectIdsAsync` 的 global 分支），round-robin 跨 project backlog 公平认领保留。
- **彻底修掉 capacity 易失的结构性根因**（顺带，非独立条目）。

## Capabilities

### New Capabilities

- `runner-management`: Runner 聚合——全局执行资源（不归属任何 project）。定义态（`runnerId`、`slots`）由控制平面拥有并持久化；运行态（`status`、接入事实、`assignedWorkflows`、`inFlightWorks`）执行端接入后观测、易失。行为：`Register` / `GoOffline` / `Claim` / `Release` / `Update(slots)`。不变量：`|assignedWorkflows| ≤ slots`；一个 workflow run 同时只在一个 Runner 的 `assignedWorkflows` 里；`offline` 不接新 workflow；runner 执行的 work 必来自它 claim 的 workflow；`slots` 以持久化定义态为准，执行端上报值不参与。

### Modified Capabilities

- `http-api`: 新增 `PATCH` 配置 `slots` 的接口（落库后下次派发生效）；register/heartbeat 携带的 `MaxWorkflowSlots` 字段对派发不再具权威性（保留为 runner 本地认知）。

## Impact

- **数据层**：新增 Runner 定义态表 + EF Core migration（`MohistDbContext`）。
- **Server / Grains**：
  - `RunnerGrain.cs`：`MaxWorkflowSlots` 改读持久化定义态；`RegisterAsync` 首次接入时初始化实体；新增 `Update(slots)` 行为；`PollAsync`（`RunnerGrain.cs:122`）仍是 slot 不变量的执行点。
  - `IRunnerGrain.cs`（`RunnerInfo` record、`RunnerCapacity`）：slots 不再源自 runner 上报字段。
  - `RunnerRegistryGrain` / `RunnerRegistryKeys`：project-scoped registry 收敛到 global。
- **Server / API**：`RunnerRoutes.cs` 新增 `PATCH` 配置接口；register/heartbeat 的 `MaxWorkflowSlots` 字段降级。消费 registry key 的调用点简化为 global：`RunnerStatusService.cs:22`、`AgentRoutes.cs`、`OpencodeRoutes.cs`、`RunnerIdentityRoutes.cs`、`RunnerWorkspaceClient.cs`、`AgentJobGrain.cs:216`。
- **CLI**：`InfoCollector.cs` 的 `MAX_CONCURRENT_WORKFLOWS` 收集保留（本地认知），不再影响 server 派发。
- **测试**：`WorkflowGrainTestHelpers`、`RunnerFailureSpecs`、`BacklogSpecs`、`RuntimeEntrySpecs` 等涉及 runner 注册/slots 的 spec 需更新（注册不再决定派发 slots，改由配置/持久化决定）。
- **关联**：Epic #13 Runner Management（配置管理方向）；#214 详情页承载 UI 配置入口（互补）。
