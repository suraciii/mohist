### Requirement: 顺序锁协调簇位于独立单一职责组合服务

stage 顺序锁的获取/释放簇（锁资源解析、获取、当前 stage 释放、按 stage 释放）SHALL 驻留在独立的组合服务 `WorkflowStageLockCoordinator` 中，位于 `packages/server/src/Mohist.Server/Workflow/Grains/`，与既有 `WorkflowReadModel` 同区域。该簇涵盖原 `WorkflowGrain` 中的 `AcquireStageLocksIfNeededAsync`、`ReleaseCurrentStageLocksAsync`、`GetSequentialLockResourceAsync`，以及 `ReleaseStageLocksAsync` 的实现。`WorkflowGrain` SHALL NOT 在自身内部内联这些实现，SHALL 通过对该服务的委托调用访问锁协调能力。

#### Scenario: 锁协调实现不在 grain 内联

- **WHEN** 检查 stage 顺序锁协调（资源解析、获取顺序、释放顺序、当前 stage 释放）的实现位置
- **THEN** 这些实现 SHALL 全部位于 `WorkflowStageLockCoordinator`
- **AND** `WorkflowGrain` SHALL NOT 在自身内部内联其实现
- **AND** grain 内 retry / rerun / rerun-from-stage / poll-work / stop 路径 SHALL 各为一次对协调服务的委托调用

#### Scenario: ReleaseStageLocksAsync 接口方法体委托到协调服务

- **WHEN** 检查 `IWorkflowGrain.ReleaseStageLocksAsync(string stage, string reason)` 的 grain 方法体
- **THEN** 接口签名 SHALL 与抽取前逐字一致
- **AND** grain 方法体 SHALL 委托到 `WorkflowStageLockCoordinator`
- **AND** 既有外部调用方（bus 侧 `WorkflowStageLockReleaseHandler`）SHALL 不加修改地继续工作

### Requirement: 锁资源解析与获取/释放语义逐字保持不变

`WorkflowStageLockCoordinator` 承载的锁资源解析、获取与释放 SHALL 与抽取前行为逐字一致。`GetSequentialLockResourceAsync` SHALL 通过 `WorkflowProfileManager.LoadStageSpecsAsync` 加载 stage spec；当 `LockBehavior` 为 null 或不为 "sequential"（忽略大小写）时返回 null，否则返回 `Resources` 中第一个非空白资源。`AcquireStageLocksIfNeededAsync` SHALL 在资源为 null 时立即返回 true 且不接触 `IWorkflowStageLockGrain`；当 projectId 缺失时 SHALL 抛出 `InvalidOperationException`；否则 SHALL 经 `WorkflowStageLockKeys.ForProjectResource` 计算 key、获取 `IWorkflowStageLockGrain`、以 `StageLockRequest(GrainKey, stage, resource, projectId)` 调用 `AcquireSequentialAsync` 并返回其 `Acquired` 字段。`ReleaseStageLocksAsync` SHALL 在资源为 null 或 projectId 缺失时直接返回；否则以 `StageLockOwner(GrainKey, stage)` 调用 `ReleaseAsync`。

#### Scenario: 资源解析规则不变

- **WHEN** 对一个 stage 调用顺序锁资源解析
- **THEN** 当 stage spec 的 `LockBehavior` 为 null 或非 "sequential" 时 SHALL 返回 null
- **AND** 当为 "sequential" 时 SHALL 返回 `Resources` 中第一个非空白资源
- **AND** 该解析 SHALL 通过 `WorkflowProfileManager.LoadStageSpecsAsync` 加载 spec

#### Scenario: 获取路径在无资源时短路

- **WHEN** `AcquireStageLocksIfNeededAsync` 解析出资源为 null
- **THEN** SHALL 立即返回 true
- **AND** SHALL NOT 调用 `IWorkflowStageLockGrain`

#### Scenario: 获取路径在 projectId 缺失时抛出

- **WHEN** `AcquireStageLocksIfNeededAsync` 解析出非空资源但 projectId 为空白
- **THEN** SHALL 抛出 `InvalidOperationException`

#### Scenario: 释放路径在无资源或无 projectId 时静默返回

- **WHEN** `ReleaseStageLocksAsync` 解析出资源为 null 或 projectId 为空白
- **THEN** SHALL 直接返回
- **AND** SHALL NOT 调用 `IWorkflowStageLockGrain`

### Requirement: 锁协调不引入新的 async 让出点且并发/持久化契约不变

`WorkflowStageLockCoordinator` 的获取/释放路径 SHALL NOT 在锁获取/释放中途引入新的 async 让出点（除既有对 `IWorkflowStageLockGrain` 与 `WorkflowProfileManager` 的既有 await 外）。`WorkflowGrain` 的 `[Reentrant]` 并发语义、持久化 JSON blob 形态、ETag 契约、事件发布顺序 SHALL 全部与抽取前一致。锁协调簇 SHALL NOT 直接执行 `_run` 上的状态变更调用；`ReleaseCurrentStageLocksAsync` MAY 读取 `CurrentStageId` 以决定释放目标，但 SHALL NOT 突变 run。

#### Scenario: 协调服务不突变 run

- **WHEN** 检查 `WorkflowStageLockCoordinator` 对 `WorkflowRun` 的访问
- **THEN** 它 SHALL 至多读取 `CurrentStageId` 以决定释放目标
- **AND** SHALL NOT 调用 `WorkflowRun` 上的状态变更方法

#### Scenario: 并发语义与持久化契约不变

- **WHEN** 在抽取后检查 grain 的 `[Reentrant]` 并发语义、持久化 JSON blob 形态、ETag 契约、事件发布顺序
- **THEN** 四者 SHALL 与抽取前逐字一致
- **AND** 抽取 SHALL NOT 在锁路径引入原 grain 不存在的新 async 让出点

### Requirement: 既有 Workflow/Grain 行为守护 spec 不加修改通过

抽取 SHALL NOT 改变 stage 顺序锁、stage 推进、retry / rerun / rerun-from-stage、dispatch 与 stop 路径的可观察行为。`packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Grain/` 下既有的 stage-lock / dispatch / retry / rerun / boundary 相关 spec 文件 SHALL 不加修改地全部通过。

#### Scenario: 锁与 dispatch 相关 spec 全部通过

- **WHEN** 在抽取后运行 `Specs/Workflow/Grain/` 下的全部 spec（含 `StageLockSpecs`、`DispatchAndLoadingSpecs`、`BoundarySpecs`、`RetryRerunSpecs`、`RerunFromStageSpecs`、`HappyPathSpecs` 等）
- **THEN** 所有 spec SHALL 通过
- **AND** 无任何 spec SHALL 被弱化、跳过或改写以适配结构改动
