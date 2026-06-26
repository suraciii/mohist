## Why

WorkflowGrain 同时承担"判下一步该做什么"（控制面）和"把工作渲染成 runner 可执行的 dispatch、解析 runner 原始输出"（执行面）两件事，是一个越层的近千行状态机。越层带来真实故障：grain 内的 work 超时定时器是无视进度的盲墙钟，正是 issue 251 work-timeout 误杀的源头；runner 状态跨进 grain 又扩大了 blast radius。现在做是因为 pull 调度已落地、翻译输入大多不依赖 grain 独占态，纠正层级的可行性已经具备。

## What Changes

- **BREAKING** WorkflowGrain 对外协议从 `PollWorkAsync → WorkDispatch?` / `ReportResultAsync(runnerId, workId, WorkResult)` 改为域语义的 work item（`Task` / `Checks` 变体）出方向、域结果（`TaskOutcome` / `CheckOutcome`）入方向。work item 只携带声明/模板，不含 dispatch id、解析值、渲染好的执行上下文。
- work item → dispatch 的翻译（`WorkflowDispatchBuilder` + `PrepareWorkAsync`/`MakeDispatchAsync`）迁到调用方（RunnerGrain 组合的 translator）；入方向 runner 格式 → 域结果的解析同样外迁。
- **BREAKING** 删除 `WorkflowWork.StageInit` 变体：stage-init 改为 eager，进入 stage 时即 init（`StageStarted` ⟹ 已 init），调用方永不可见 stage-init。`WorkflowWork` 从 3 变体收敛为 2 变体（task/checks）。
- 删除控制面越界的监督：`WorkCompletionTimeout` / `FailTimedOutWorkAsync` / work 完成定时器、`IWorkflowGrain.NotifyRunnerLostAsync` / `FailLostRunningTasksAsync` / 域 `WorkflowRun.FailTaskForRunnerLost`。WorkflowGrain 零定时器、零 runner 概念。
- 新增执行面兜底：RunnerGrain 持 outstanding-work 集，runner 丢失时经正常 report 通道合成 `failed`。
- 配套清理：读模型提取（grain 内组合）、锁协调迁 eventbus 订阅者（`StageCompleted/Failed → ReleaseStageLocksAsync`）、删除半死 backlog。
- 摆脱对 `WorkflowDefinition` 整体对象依赖：grain 零处引用 `WorkflowDefinition`；profileManager 提供窄 API（`LoadStageSpecsAsync` / `LoadStructureAsync` / `LoadApprovalConfigAsync`），整模板选择封装其内部；`LoadEffectiveDefinitionAsync` 删除。
- server work item 与 runner TS 的 `WorkItem` 镜像同一形状。

## Capabilities

### New Capabilities

- `workflow-work-item-protocol`: WorkflowGrain 对外公开的域语义 work item 协议——出方向 `Task`/`Checks` 变体（携带声明/模板、不含 dispatch 信息）、入方向 `TaskOutcome`/`CheckOutcome` 域结果，以及 stage-init eager 化、`StageInit` 变体删除等对外契约。
- `workflow-supervision`: 运行时监督分层——work 超时归 runner 进程、runner 丢失归 RunnerGrain（outstanding 集 + 合成 `failed`）、WorkflowGrain 零定时器/零 runner 概念；删除控制面越界的 work 超时与 runner-lost 通知路径。
- `workflow-translation`: work item ↔ dispatch/result 的翻译层归属与边界——翻译在调用方（RunnerGrain 一侧）完成，grain 不再承担变量解析/上下文装配/prompt 加载/dispatch 构建（出）与 runner 结果解析（入）。
- `workflow-profile-resolution`: grain 摆脱对 `WorkflowDefinition` 整体对象的依赖；profileManager 提供窄 API（`LoadStageSpecsAsync`/`LoadStructureAsync`/`LoadApprovalConfigAsync`），整模板选择级联封装其内部。

### Modified Capabilities

（无。当前 `openspec/specs/` 下无 workflow 调度/分发/监督相关 spec，本次为新建。）

## Impact

- **Server / 控制面（`packages/server/`）**：`WorkflowGrain` 及其接口 `IWorkflowGrain` 重设计（协议方法、删除超时/runner-lost 路径）；域模型 `WorkflowRun`/`WorkflowWork`（`StageInit` 删除、`Advance`/`InitializeStage` eager 化、`FailTaskForRunnerLost` 删除）；读模型提取；锁协调改 eventbus 订阅；`LoadEffectiveDefinitionAsync` 删除。
- **Server / 执行面**：`RunnerGrain` 新增 outstanding-work 记账与 runner-loss 合成失败；承接迁移来的翻译层（`WorkflowDispatchBuilder` 及入方向解析）。
- **profileManager**：新增 `LoadStageSpecsAsync`/`LoadStructureAsync`/`LoadApprovalConfigAsync`，整模板选择封装其内部；`WorkflowDefinition` 降为内部细节。
- **Runner（`packages/runner/`）**：TS `WorkItem` 形状与 server work item 对齐（而非镜像 dispatch 反序列化产物）；runner 进程侧 liveness/probe/maxDuration 校准。
- **领域事件**：`StageStarted` 语义调整（⟹ 已 init）；锁协调改订阅触发的相关事件流。
- **测试**：现有 workflow 系统测试需适配新协议形状；翻译层、监督分层、profile 窄 API 需新增测试。
- **依赖**：无新增外部依赖。
- **风险**：high —— 核心域持久化状态机，协议变更 blast radius 跨控制/执行面，回滚成本高。
