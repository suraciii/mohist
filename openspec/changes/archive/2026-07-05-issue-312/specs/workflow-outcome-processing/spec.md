### Requirement: outcome 处理簇位于独立单一职责组合服务

task / check outcome 处理簇 SHALL 驻留在独立的组合服务 `WorkflowOutcomeProcessor` 中，位于 `packages/server/src/Mohist.Server/Workflow/Grains/`，与既有 `WorkflowReadModel` 同区域。该簇涵盖原 `WorkflowGrain` 中的 `ProcessTaskOutcomeAsync`、`ProcessCheckOutcomeAsync`、`ResolveRepairTasks`、`TryScheduleRequestedCheckRepairAsync`、`ClearExecutableStateAsync`、`MarkTaskRunningAsync`、`MarkChecksRunning`、`ToWorkItemAsync`、`TryBuildActiveWorkItem`。`WorkflowGrain` SHALL NOT 在自身内部内联这些实现，SHALL 通过对该服务的委托调用访问 outcome 处理能力。

#### Scenario: outcome 处理实现不在 grain 内联

- **WHEN** 检查 task / check outcome 处理（task outcome 应用、check 结果裁决、repair 任务解析、check-repair 调度、可执行态清理、task/checks 置 running、work item 构造）的实现位置
- **THEN** 这些实现 SHALL 全部位于 `WorkflowOutcomeProcessor`
- **AND** `WorkflowGrain` SHALL NOT 在自身内部内联其实现
- **AND** grain 内 `ReportTaskOutcomeAsync` / `ReportCheckOutcomeAsync` / `RetryAsync` / `PollWorkAsync` / `StopAsync` 路径 SHALL 各为一次对该服务的委托调用

### Requirement: 服务按引用接收可变 WorkflowRun 并在传入对象上突变

`WorkflowOutcomeProcessor` SHALL 按引用接收可变的 `WorkflowRun`，而非只读快照。所有对 run 的状态变更（写 `currentTask.Output`、调用 `run.CompleteTask()` / `run.FailTask()` / `run.AddRuntimeTasks()` / `run.ProcessCheckResults()` / `run.StartTask()` / `run.FailTaskForStopped()` / `run.ScheduleCheckRepair()` / `run.ResolveFeedback()` 等）SHALL 发生在传入的对象上。这是共享突变而非纯委托：传入的 `WorkflowRun` 引用 SHALL 与 grain 的 `_run` 指向同一对象，服务对其的写入 SHALL 对 grain 立即可见。

#### Scenario: 传入的是可变引用而非快照

- **WHEN** grain 委托 `WorkflowOutcomeProcessor` 处理一次 outcome
- **THEN** 服务 SHALL 接收 grain 当前 `_run` 的可变引用
- **AND** SHALL NOT 接收一份只读拷贝/快照

#### Scenario: 突变发生在传入对象上且对 grain 可见

- **WHEN** 服务在处理过程中写入 `currentTask.Output` 或调用 `run.CompleteTask()` / `run.FailTask()` / `run.AddRuntimeTasks()`
- **THEN** 这些写入 SHALL 落在传入的 `WorkflowRun` 引用上
- **AND** grain 在委托返回后观察到的 `_run` SHALL 反映这些写入

### Requirement: 服务接收 grain 的 CommitAsync 回调以处理中途事件

`WorkflowOutcomeProcessor` 中需要在处理中途持久化并发布事件的方法（`MarkTaskRunningAsync` 经 `_sessionHealth.CheckAndEnforceAsync` 触发的 commit、`ClearExecutableStateAsync` 中对运行中任务的 `FailTaskForStopped` 保存）SHALL 通过传入的 `CommitAsync` 回调执行，而非直接持有 `IWorkflowRunStore`。回调签名 SHALL 与既有 `WorkflowSessionHealthService` 所用的 `Func<IReadOnlyList<WorkflowEvent>, Task>` 一致。

#### Scenario: 中途 commit 经回调而非直连 store

- **WHEN** `MarkTaskRunningAsync` 因 session health gate 触发需要 commit 事件
- **THEN** SHALL 调用传入的 `CommitAsync` 回调
- **AND** SHALL NOT 直接调用 `IWorkflowRunStore`

#### Scenario: ClearExecutableStateAsync 对运行中任务的保存路径不变

- **WHEN** `ClearExecutableStateAsync` 发现当前 stage 存在运行中任务
- **THEN** SHALL 调用 `run.FailTaskForStopped(reason)` 产生事件
- **AND** SHALL 经 grain 的保存路径持久化这些事件
- **AND** 该路径 SHALL 与抽取前行为一致

### Requirement: task outcome 处理语义逐字保持不变

`ProcessTaskOutcomeAsync` SHALL 与抽取前行为一致。当 `outcome.Artifacts` 非空时 SHALL 为每个 artifact 追加一条 `WorkflowArtifactRecorded(GrainKey, taskRunId, path, now)` 事件。当状态为 `Passed` 时 SHALL 设置 `currentTask.Output`（经输出 JSON 解析）、若 `currentTask.CausedByFeedbackId` 存在则调用 `run.ResolveFeedback`、追加 `run.CompleteTask()` 的事件、并在 `outcome.AddTasks` 非空时解析为 `TaskDefinition` 并经 `run.AddRuntimeTasks` 追加。当状态非 `Passed` 时 SHALL 设置 `currentTask.Output` 并以 `TaskResult("failed", detail ?? output)` 调用 `run.FailTask`。输出解析 SHALL 复用既有「先 JSON parse 失败回退为 JSON 字符串」的规则。

#### Scenario: Passed 路径完成 task 并解析 feedback

- **WHEN** `ProcessTaskOutcomeAsync` 收到 `Passed` outcome 且 `currentTask.CausedByFeedbackId` 非空
- **THEN** SHALL 设置 `currentTask.Output`
- **AND** SHALL 调用 `run.ResolveFeedback(feedbackId, taskId, output)`
- **AND** SHALL 追加 `run.CompleteTask()` 产生的事件

#### Scenario: Passed 路径展开 AddTasks

- **WHEN** `ProcessTaskOutcomeAsync` 收到 `Passed` outcome 且 `outcome.AddTasks` 非空
- **THEN** SHALL 将每个 AddTask 解析为 `TaskDefinition`（经 `WorkflowDispatchHelpers.ParseWith`）
- **AND** SHALL 调用 `run.AddRuntimeTasks` 并把产生的事件并入返回列表

#### Scenario: 非 Passed 路径失败 task

- **WHEN** `ProcessTaskOutcomeAsync` 收到非 `Passed` outcome
- **THEN** SHALL 设置 `currentTask.Output`
- **AND** SHALL 以 `TaskResult("failed", detail ?? output)` 调用 `run.FailTask`

#### Scenario: artifact 事件被记录

- **WHEN** `ProcessTaskOutcomeAsync` 收到的 outcome 携带非空 `Artifacts`
- **THEN** SHALL 为每个 artifact 追加一条 `WorkflowArtifactRecorded` 事件
- **AND** 事件顺序 SHALL 保持在 task 完成事件之前

### Requirement: check outcome 裁决与 repair 解析语义逐字保持不变

`ProcessCheckOutcomeAsync` SHALL 与抽取前行为一致：加载当前 stage 的 stage spec，逐个 result 裁决——`pass` 产生 pass action、`pending` 产生 pending action、其它情况经 `ResolveRepairTasks` 尝试构造 repair action（无 repair 则 fail action）；一旦某 result 解析出 repair tasks SHALL 立即 break 并以该 repair action 收尾。`ResolveRepairTasks` SHALL 查找 check 定义的 `OnFailure.Repair`，在 `enforceLimit` 为真时按 `run.GetRepairCount(checkName) >= repair.Limit` 短路返回 null，否则经 `run.BuildRepairTasks` 构造。最终 SHALL 调用 `run.ProcessCheckResults(actions)` 返回事件。

#### Scenario: repair 在首个可修 result 处收尾

- **WHEN** `ProcessCheckOutcomeAsync` 遇到第一个能解析出 repair tasks 的失败 result
- **THEN** SHALL break 后续 result 处理
- **AND** SHALL 以该 repair action 收尾 actions 列表
- **AND** SHALL 调用 `run.ProcessCheckResults(actions)`

#### Scenario: repair 限额被尊重

- **WHEN** `ResolveRepairTasks` 以 `enforceLimit = true` 调用且 `run.GetRepairCount(checkName) >= repair.Limit`
- **THEN** SHALL 返回 null（不再生成 repair tasks）

#### Scenario: TryScheduleRequestedCheckRepair 仅在 CheckUnrepaired 失败时触发

- **WHEN** `TryScheduleRequestedCheckRepairAsync` 在 `run.Status != Failed` 或 `failure.Reason != CheckUnrepaired` 或 `failure.CheckName` 为空时调用
- **THEN** SHALL 返回 null
- **AND** SHALL NOT 调用 `run.ScheduleCheckRepair`
- **WHEN** 条件满足且 `ResolveRepairTasks(enforceLimit: false)` 解析出 tasks
- **THEN** SHALL 清理当前 stage 的 checks 运行态并调用 `run.ScheduleCheckRepair(checkName, repairTasks, message)`

### Requirement: 可执行态清理与置 running 语义逐字保持不变

`ClearExecutableStateAsync` SHALL 先委托释放当前 stage 锁（经锁协调簇），再清理当前 stage 的 checks 运行态（`ChecksWorkId = null`、运行中 check 回退 Pending），若存在运行中 task 则调用 `run.FailTaskForStopped(reason)` 并保存，否则仅保存。`MarkTaskRunningAsync` SHALL 先经 `WorkflowSessionHealthService.CheckAndEnforceAsync` 检查；若 task 已 Running 则返回其 `WorkId ?? logicalTaskId`；否则调用 `run.StartTask(workId, runnerId)`、保存并派发事件，最后记录 `_lastKnownRunnerId`。`MarkChecksRunning` SHALL 生成 `checks-{stage}:{guid}` 形式的 checksWorkId、写入 `currentStage.ChecksWorkId`、把匹配 check 置 Running 并设置 `StartedAt`、把 `run.Status` 置为 `Running`。`ToWorkItemAsync` 与 `TryBuildActiveWorkItem` 的 task/checks 分支与 runner 匹配规则 SHALL 与抽取前逐字一致。

#### Scenario: ClearExecutableStateAsync 先释放锁再清理可执行态

- **WHEN** `ClearExecutableStateAsync(reason)` 被调用
- **THEN** SHALL 先释放当前 stage 锁
- **AND** SHALL 清理当前 stage 的 checks 运行态
- **AND** 当存在运行中 task 时 SHALL 调用 `run.FailTaskForStopped(reason)` 并保存事件

#### Scenario: MarkTaskRunningAsync 复用既有 session health 与 StartTask 路径

- **WHEN** `MarkTaskRunningAsync` 处理一个逻辑 task
- **THEN** SHALL 先经 `WorkflowSessionHealthService.CheckAndEnforceAsync` 检查
- **AND** 当 task 已 Running 时 SHALL 直接返回 `WorkId ?? logicalTaskId`
- **AND** 否则 SHALL 调用 `run.StartTask(workId, runnerId)`、保存并派发事件

#### Scenario: MarkChecksRunning 生成 checksWorkId 并置 Running

- **WHEN** `MarkChecksRunning(stage, items)` 被调用
- **THEN** SHALL 生成 `checks-{stage}:{guid}` 形式的 checksWorkId
- **AND** SHALL 把匹配 check 置 Running 并设置 `StartedAt`
- **AND** SHALL 把 `run.Status` 置为 `Running`

### Requirement: 事务内保存→追加/发布事件顺序与 ETag 冲突重载路径不变

抽取 SHALL NOT 改变「事务内保存状态 → 追加/发布事件」的顺序，SHALL NOT 在 run 突变中途引入新的 async 让出点，SHALL NOT 破坏 ETag 冲突时 `SaveRunAsync` 内 `DeactivateOnIdle()` 重载路径。`WorkflowGrain` 的 `[Reentrant]` 并发语义、持久化 JSON blob + ETag、`IWorkflowGrain` 接口签名与 `[GenerateSerializer]` record 字段顺序、事件发布顺序 SHALL 全部不变。

#### Scenario: 保存→发布顺序保持

- **WHEN** 检查抽取后 `ReportTaskOutcomeAsync` / `ReportCheckOutcomeAsync` 的提交路径
- **THEN** SHALL 保持「事务内保存 run → 再发布事件」的既有顺序
- **AND** SHALL NOT 在 run 突变之后、保存之前引入新的 await 让出点

#### Scenario: ETag 冲突仍触发 grain 重载

- **WHEN** 保存时抛出 `DbUpdateConcurrencyException`
- **THEN** 既有 `DeactivateOnIdle()` 重载路径 SHALL 完整保留
- **AND** 该行为 SHALL 不因抽取而改变

#### Scenario: 接口与持久化契约不变

- **WHEN** 检查抽取后的 `IWorkflowGrain` 方法签名、`[GenerateSerializer]` record 字段顺序、持久化 JSON blob 形态、`[Reentrant]` 语义
- **THEN** 四者 SHALL 与抽取前逐字一致

### Requirement: 既有 Workflow/Grain 行为守护 spec 不加修改通过

抽取 SHALL NOT 改变 task / check outcome 处理、repair 调度、check loop、artifact 记录、retry / stop 清理、session health gate 集成等可观察行为。`packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Grain/` 下既有的 outcome / check-recovery / check-retry / artifact / task-output / failure / session-health 相关 spec 文件 SHALL 不加修改地全部通过。

#### Scenario: outcome 与 check 相关 spec 全部通过

- **WHEN** 在抽取后运行 `Specs/Workflow/Grain/` 下的全部 spec（含 `TaskOutputCaptureSpecs`、`CheckRecoverySpecs`、`CheckRetrySpecs`、`ChecksParallelSpecs`、`WorkflowArtifactBindingSpecs`、`WorkflowCheckLoopArtifactSpecs`、`FailureSpecs`、`WorkflowRetrySessionHealthGuardSpecs`、`WorkflowRunContextExhaustionBlockSpecs` 等）
- **THEN** 所有 spec SHALL 通过
- **AND** 无任何 spec SHALL 被弱化、跳过或改写以适配结构改动
