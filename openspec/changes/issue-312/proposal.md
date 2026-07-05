## Why

`WorkflowGrain` 仍是 972 行的核心域 grain，命令侧的 stage-lock 协调与 outcome 处理长期内联在 grain 里。epic #22 在 Scope 里承诺「分离 Workflow 编排 Grain 的查询方法与状态变更方法」，但 6 个 issue 没有一项真正落地命令侧——查询侧已委托给 `WorkflowReadModel`、HTTP API 走独立 `WorkflowQuerier`、`RunnerGrain` 读 `WorkflowRunQuerier`，命令侧仍是空白。现在做是因为这是纯内部重排：grain 接口、持久化 schema、`[Reentrant]` 并发语义、事件发布顺序全部不变，35 个 spec 文件（~8k 行）守护行为，风险由 epic 标的 high 下调为 medium。

## What Changes

- stage-lock 协调簇（`AcquireStageLocksIfNeededAsync` / `ReleaseCurrentStageLocksAsync` / `ReleaseStageLocksAsync` / `GetSequentialLockResourceAsync`）抽到独立的 `WorkflowStageLockCoordinator`。该簇不依赖 `_run` 突变，风险最低，先抽。
- outcome 处理簇（`ProcessTaskOutcomeAsync` / `ProcessCheckOutcomeAsync` / `ResolveRepairTasks` / `TryScheduleRequestedCheckRepairAsync` / `ClearExecutableStateAsync` / `MarkTaskRunningAsync` / `MarkChecksRunning` / `ToWorkItemAsync` / `TryBuildActiveWorkItem`）抽到独立的 `WorkflowOutcomeProcessor`，按引用接收可变 `WorkflowRun` + grain 的 `CommitAsync` 回调。
- `InitializeFreshStagesAsync` 抽到 `WorkflowStageInitializer`，仍在 `CommitAsync` 内、保存前执行。
- `WorkflowGrain` 本体收敛为 装载/保存/派发/委托；`PollWorkAsync`（查询签名 + 命令副作用混合）与 `GetAssignedRunnerIdAsync`（读未持久化的 `_lastKnownRunnerId`）保留在 grain 内。
- **BREAKING（仅 internal surface）**：上述 private 成员移出 grain，对程序集外不可见。`IWorkflowGrain` 接口签名、`[GenerateSerializer]` record 字段、持久化 JSON blob + ETag、`[Reentrant]` 并发语义、事件发布顺序——全部不变。
- 不变量：抽取不得在突变中途引入新的 async 让出点；不得改变「事务内保存状态 → 追加/发布事件」的顺序；不得破坏 ETag 冲突时 `DeactivateOnIdle()` 重载路径。
- **关键耦合差异**：`WorkflowSessionHealthService` 只**读** `run` 后回调 commit（纯委托），而 `ProcessTaskOutcomeAsync` / `ProcessCheckOutcomeAsync` 会**直接突变**传入的 `WorkflowRun`（写 `currentTask.Output`、调 `run.CompleteTask()` / `run.FailTask()` / `run.AddRuntimeTasks()`）。抽取时传入的是可变 `WorkflowRun` 引用而非只读快照——这是共享突变而非纯委托，约束更强。

## Capabilities

- `workflow-stage-lock-coordination`: stage 顺序锁的获取/释放簇收敛为独立 composed 服务，由 grain 委托调用；锁资源解析、获取/释放顺序、与 `ReleaseStageLocksAsync` 接口方法的语义不变，且不引入突变中途的 async 让出点。
- `workflow-outcome-processing`: task/check outcome 处理簇收敛为独立 composed 服务，按引用接收可变 `WorkflowRun` + `CommitAsync` 回调；突变发生在传入对象上（`CompleteTask` / `FailTask` / `AddRuntimeTasks` / `ProcessCheckResults` 等），「事务内保存 → 追加/发布事件」顺序与 ETag 冲突重载路径不变。
- `workflow-stage-initialization`: fresh stage 初始化（`InitializeFreshStagesAsync`）从 grain 抽到独立初始化器，仍在 `CommitAsync` 内、持久化保存之前执行，初始化结果与执行时机不变。

## Impact

- **Server 实现**（`packages/server/src/Mohist.Server/Workflow/Grains/`）：
  - 新增 `WorkflowStageLockCoordinator.cs`、`WorkflowOutcomeProcessor.cs`、`WorkflowStageInitializer.cs`，与既有 `WorkflowReadModel.cs` 同区域。
  - `WorkflowGrain.cs` 移除上述 private 成员，保留对三个新类型的委托调用；构造处按 `WorkflowReadModel` 模式（内联组合）或 `WorkflowSessionHealthService` 模式（DI 注入）二选一，按各自耦合度决定。
  - `WorkflowRun` 域突变方法（`Workflow/Domain/Run/WorkflowRun.Task.cs` 等）不动——新服务通过传入引用调用它们。
- **Server 测试**（`packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Grain/`）：现有 35 个 spec 文件作为行为守护，全部通过、不改；行为等价性由集成 spec 兜底。
- **无 API/契约/存储/Runner/Web/CLI 变化**：`IWorkflowGrain` 方法签名与 `[GenerateSerializer]` record 字段顺序不变；持久化 JSON blob + ETag 不变；`[Reentrant]` 并发模型不变；事件发布顺序不变；HTTP 响应字节不变。
- **无迁移、无配置变化**。
