## Why

WorkflowRun status 查询是 Server 上频率最高的整载读：`mo run watch` 3s 轮询叠加 runner 高频上报，每次都经 `WorkflowQuerier.GetStatusAsync` 把整条 `State`（实测平均 325 KB、最大 3.6 MB）完整 `JSON.Deserialize<WorkflowRun>`，即便 State 内容自上次查询起未变。反序列化 + STJ 字符串转码是 LOH 分配风暴的主源（占 LOH 分配 95%+），直接推高进程 RSS 峰值。#537 已消除 State 体积的主源（dispatch 快照外置），但读路径的频率放大仍让每一次未变的 State 都重复付出整载反序列化代价。行上已有维护良好的 `ETag`（每次 State 实际写时递增，迁移幂等保证），设计 spec（`design/workflow/run-state.md:92`）已定稿"行 ETag 可用于版本化缓存"——本 issue 把那条已 spec 的读路径优化落地。

## What Changes

- `WorkflowQuerier.GetStatusAsync` 引入 ETag 版本化缓存：先用轻量投影读 `ETag`（不反序列化 `State`），命中缓存（ETag 未变）时直接返回缓存的 status view，跳过整条 State 反序列化与 LOH 分配。
- ETag 变化时执行一次完整反序列化重建 view，并刷新缓存条目；缓存正确性以"重建结果与不缓存时逐字节一致"为底线。
- 复用 `WorkflowRuns` 行上既有的 `ETag` 列（`WorkflowRunStore` 每次实际写入时递增、冷启动迁移幂等维护），不新增列、不改写放大。
- status 对外契约形状不变：调用方（API 路由、`IssueGrain`、`WorkflowActivityQuerier`、`AgentActivityFeedAssembler` 等）拿到的 `WorkflowStatusView?` 与字段集完全一致。
- artifact 摘要随 view 一起正确反映最新状态——artifacts 存于独立表、变化不推进 State ETag，缓存的失效契约必须同时覆盖 State 变化与 artifact 变化两个维度（具体机制见 design.md），不得向调用方返回过期 artifact 摘要。

非目标：改 status 契约形状；改 State 写路径或写放大；改其他整载读路径（`GetWorkspaceAsync`、`GetRepositoryContextAsync`、日志路径——#538）；引入跨进程分布式缓存。

## Capabilities

- `workflow-run-status-cache`: status 查询读路径的 ETag 版本化缓存——ETag 未变命中缓存跳过 State 反序列化、ETag 变化触发一次反序列化并刷新缓存、缓存结果与无缓存时逐字节等价、artifact 变化也被正确反映（不返回过期摘要）、对外契约形状不变。

## Impact

- **Server — status 查询** (`packages/server/src/Mohist.Server/Workflow/Services/WorkflowQuerier.cs:35` `GetStatusAsync`): 当前无条件 `Hydrate(row)`（整载 `JSON.Deserialize<WorkflowRun>(row.State)`）后 `BuildStatusView` + `AttachArtifactSummariesAsync`；改为先投影读 ETag、按 ETag 命中/重建。
- **Server — artifact 摘要装配** (`WorkflowQuerier.cs:54` `AttachArtifactSummariesAsync`, `IWorkflowArtifactQuerier`): artifact 独立于 State ETag 变化，缓存失效契约须覆盖之（重建时机或缓存键纳入 artifact 版本）。
- **Server — ETag 来源** (`packages/server/src/Mohist.Server/Infrastructure/Data/Workflow/WorkflowRunStore.cs:156,166`): 既有 `ETag` 列的递增与幂等规则（`design/workflow/run-state.md:59-66`）为本缓存的版本权威，不改动。
- **Server — 调用方**: API 路由（`WorkflowRoutes`、`WorkflowRoutes.Detail`、`WorkflowRoutes.WorkflowControl`）、`IssueGrain`、`WorkflowActivityQuerier`、`AgentActivityFeedAssembler` 均经 `GetStatusAsync`，契约不变，透明受益。
- **测试**: 回归覆盖 ETag 未变不触发反序列化、ETag 变化触发恰好一次反序列化、缓存 view 与无缓存逐字节一致、artifact 新增/变化后 status 反映最新；不得依赖墙钟或真实并发，缓存可注入时钟/版本源。
- **文档/spec**: `design/workflow/run-state.md:92` 的差距条目（"`GetStatusAsync` 无缓存；行上已有 ETag 列，可用于版本化缓存"）由本 issue 关闭。
