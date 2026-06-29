### Requirement: WorkflowGrain 零定时器零 runner 概念

WorkflowGrain SHALL 不持有任何定时器，SHALL NOT 感知 runner 死活。它 SHALL 只消费 work 结果。runner 状态 SHALL 永远不跨进 WorkflowGrain。

#### Scenario: 不持有 work 超时定时器

- **WHEN** 一个 work 被拉取后处于 STARTED
- **THEN** WorkflowGrain SHALL NOT 为该 work 装配任何完成定时器
- **AND** `WorkflowGrainOptions.WorkCompletionTimeout`、`FailTimedOutWorkAsync`、`WorkCompletionDueTime`、`ArmWorkCompletionTimer`、`_workCompletionTimer` SHALL 被删除

#### Scenario: 不接收 runner-lost 通知

- **WHEN** 一个 runner 被判定丢失
- **THEN** WorkflowGrain SHALL NOT 收到 runner-lost 通知
- **AND** `IWorkflowGrain.NotifyRunnerLostAsync`、`WorkflowGrain.FailLostRunningTasksAsync`、域 `WorkflowRun.FailTaskForRunnerLost` SHALL 被删除

### Requirement: work 执行超时归 runner 进程

work 执行超时（卡死 / runaway）SHALL 主要由 runner 进程判定，因为只有 runner 进程拥有进度信号（token 流、子进程存活）。runner 进程 SHALL 在到点时上报 `failed`。

此外，RunnerGrain SHALL 作为控制面安全网，对每个被取用的 work 强制执行统一的 `WorkCompletionTimeout`（默认 30min，`Mohist:Workflow` 配置段），覆盖 runner 进程自身判定失效的场景（runner 进程活着但单个 work 不再上报，或 server 与 runner 同步重启后 runner 进程未恢复监督）。超时 SHALL 经现有 `ReportWorkflowResultAsync` 通道合成 `WorkResult(status="failed", reason="timeout")` 上报，与 runner-loss 合成同款；SHALL NOT 引入新的结果状态。

#### Scenario: 卡死工作由 runner 进程判失败

- **WHEN** 一个工作在 runner 进程中表现为卡死（超过 liveness quiet 阈值）
- **THEN** runner 进程 SHALL 通过 probe 检测并上报 `failed`
- **AND** WorkflowGrain SHALL NOT 自行基于墙钟判定超时

#### Scenario: maxDuration 与 quiet 阈值分离

- **WHEN** runner 进程配置 liveness
- **THEN** maxDuration 帽（抓 runaway）SHALL 与 quiet 阈值（抓卡死）分离
- **AND** 两者 SHALL NOT 在同一时刻同时触发以致互相无冗余
- **AND** maxDuration SHALL NOT 被设成与 grain 旧值相同的 20 分钟

#### Scenario: RunnerGrain 控制面安全网兜底

- **WHEN** 一个 work 被取用后超过 `WorkCompletionTimeout` 仍未上报结果，且 runner 进程未自行判失败（runner 活着但 work 卡住，或 server+runner 同步重启）
- **THEN** RunnerGrain SHALL 合成 `WorkResult(status="failed", reason="timeout")` 经 `ReportWorkflowResultAsync` 上报
- **AND** SHALL NOT 引入新的 work 结果状态（与现有 `passed|failed + detail` 约定对齐）

#### Scenario: 统一超时不 per-task

- **WHEN** 配置 `WorkCompletionTimeout`
- **THEN** 该超时 SHALL 是全局统一的（默认 30min），SHALL NOT 按 task / stage 差异化

### Requirement: runner 丢失由 RunnerGrain 兜底合成失败

runner 丢失（心跳失联）SHALL 由 RunnerGrain 检测。RunnerGrain SHALL 持有一个持久的 `RunnerWorks` 台账，覆盖该 runner 上的所有 work（workflow work + agent-job work），并在 grain 激活时从台账一次性 hydrate `outstanding` 行进内存。检测到 runner 丢失时，RunnerGrain SHALL 遍历 outstanding 集，经正常 report 通道（`ReportWorkflowResultAsync`）合成 `failed` 上报。

work 完成超时与 runner 丢失 SHALL 共用同一 report 通道合成失败，且 SHALL 互不干扰：一条 work 在任一触发条件下进入终态后，SHALL NOT 被另一触发条件再次合成。

#### Scenario: RunnerGrain 持久记账 outstanding work

- **WHEN** 一个 workflow work 或 agent-job work 被分配执行
- **THEN** RunnerGrain SHALL 在 `RunnerWorks` 台账中记账该 work（`status=outstanding`）
- **AND** 当前 `ReportWorkflowResultAsync` 作为纯 relay 不记账的行为 SHALL 被补齐

#### Scenario: runner 丢失触发合成失败

- **WHEN** RunnerGrain 检测到一个 runner 丢失（心跳失联）
- **THEN** RunnerGrain SHALL 遍历该 runner 的 outstanding-work 集
- **AND** SHALL 经正常 report 通道为每个 outstanding work 合成 `failed` 上报
- **AND** WorkflowGrain 收到的 SHALL 与普通失败无异的 work 结果

#### Scenario: 超时与 runner-loss 合成互不干扰

- **WHEN** 一条 work 因超时（`reason=timeout`）进入终态
- **THEN** 后续的 runner-loss 扫描 SHALL NOT 再次合成该 work
- **AND** 反之，因 runner-loss（`reason=runner-lost`）进入终态的 work SHALL NOT 被超时扫描再次合成

### Requirement: runner-loss 检测须持久化

runner-loss 检测 SHALL 基于 Orleans reminder 与持久化心跳状态，而 NOT 基于 grain 定时器与内存 `_lastHeartbeat`。否则 silo 重启 + runner 永久消失时无人触发 closeout。此项为独立健壮性 bug，可单开 follow-up issue 跟踪。

#### Scenario: 检测跨 silo 重启存活

- **WHEN** silo 重启且某 runner 永久消失
- **THEN** runner-loss 检测 SHALL 仍被触发（基于 reminder + 持久化心跳）
- **AND** SHALL NOT 因内存 `_lastHeartbeat` 丢失而失效

#### Scenario: follow-up 可跟踪

- **WHEN** 本次变更不实现 reminder 化检测
- **THEN** 该项 SHALL 通过单开的 follow-up issue 跟踪
- **AND** 验收 SHALL 接受"已开 follow-up issue 跟踪"作为满足条件

### Requirement: RunnerWorks 持久台账

RunnerGrain SHALL 维护一张 `RunnerWorks` 持久台账，记录发生在一个 runner 上的所有 work（workflow + agent-job）的全生命周期。台账 SHALL 在取用时 insert、在 report 或合成时 update 为终态，SHALL NOT delete 终态行（保留历史）。

台账状态 SHALL 扁平为 `outstanding | completed | failed`；`timeout` / `runner-lost` / 具体 message 仅作为 `Reason` 字段填充于 `Status=failed` 的行，SHALL NOT 新增状态枚举。`TakenAt`（取用时刻）SHALL 作为完成超时的起算点被记录与持久化。

台账与 `WorkflowRuns.TaskRun` 的分工：TaskRun 是 workflow 域 task 生命周期权威；RunnerWorks 是派发视角台账（按 runner 查、含 agent job、承载合成态）。agent-job work SHALL 仅在 `RunnerWorks` 有家。

#### Scenario: 取用时插入台账行

- **WHEN** RunnerGrain 取用一个 work（`PollOneWorkflowAsync` 或 `AssignAgentJobAsync`）
- **THEN** SHALL 在 `RunnerWorks` 插入一行：`Status=outstanding`、`TakenAt=取用时刻`、`OwnerKind`（workflow | agent-job）、`OwnerId`、`WorkId`
- **AND** 该 `TakenAt` SHALL 经注入的 `TimeProvider` 读取，SHALL NOT 直接用 `DateTime(Utc)Now`

#### Scenario: 终态更新不删除

- **WHEN** 一个 work report 成功或被合成失败
- **THEN** 对应台账行 SHALL 被更新为 `completed` 或 `failed`（含 `Reason`、`FinishedAt`）
- **AND** SHALL NOT 被删除
- **AND** 终态行 SHALL NOT 再发生状态转换

#### Scenario: 状态扁平失败原因入 Reason

- **WHEN** 一个 work 因超时失败
- **THEN** 台账行 SHALL 为 `Status=failed`、`Reason=timeout`
- **WHEN** 一个 work 因 runner 丢失被合成失败
- **THEN** 台账行 SHALL 为 `Status=failed`、`Reason=runner-lost`
- **AND** SHALL NOT 存在 `timeout` / `runner-lost` 等独立状态枚举值

#### Scenario: 激活时 hydrate outstanding 集

- **WHEN** RunnerGrain 被激活（含 silo 重启后重新激活）
- **THEN** SHALL 从 `RunnerWorks` 一次性读取所有 `Status=outstanding` 的行 hydrate 进内存 active 集
- **AND** reminder tick SHALL 只扫内存对象，SHALL NOT 每 tick 读 DB

#### Scenario: agent-job work 在台账安家

- **WHEN** 一个 agent-job work 被分配
- **THEN** `RunnerWorks` SHALL 是该 work 的唯一台账归属
- **AND** SHALL NOT 期望在 `WorkflowRuns` 中存在该 work 的记账

#### Scenario: 恢复 task 作为新 work 走新 deadline

- **WHEN** 一个因超时失败的 task 被恢复（新 attempt）
- **THEN** 恢复产生的新 work（新 workId）SHALL 在取用时插入新的台账行
- **AND** 新行 SHALL 自带新的 `TakenAt` 与新的完成超时 deadline
- **AND** SHALL NOT 继承前次失败行的任何状态

### Requirement: Work 完成超时检测由持久 reminder 驱动

RunnerGrain SHALL 注册一个 per-runner 的 Orleans reminder（如 `work-timeout`）驱动 work 完成超时检测，SHALL NOT 使用 grain timer。reminder 持久化进 `OrleansRemindersTable`，在 grain 去活或 silo 重启后 SHALL 重新激活 grain 来 fire，从而覆盖 server+runner 同步重启后 outstanding work 成为孤儿的场景（issue #275）。

该 reminder SHALL 以 register-if-absent 语义维护：仅在 outstanding work 从零变为非零时注册一次（先 `GetReminder` 确认不存在再注册），SHALL NOT 在 reminder 已存在时于后续 work 分配（`PollOneWorkflowAsync` / `AssignAgentJobAsync`）中再次 `RegisterOrUpdateReminder` 以重置 due-time。reminder SHALL 在仍有任何 pending 或 running work 时保持稳定的 runner 级周期；每条 work 的完成 deadline SHALL 继续由其自身的 `TakenAt` 派生，而非由最近一次 reminder 注册时刻派生。reminder SHALL 仅在一次扫描观察到无 pending 或 running work 时被注销/停止（drain 行为保留）。合成路径（`WorkResult(status="failed", reason="timeout")` 经 `ReportWorkflowResultAsync`）保持不变。

#### Scenario: 使用持久 reminder 而非 grain timer

- **WHEN** RunnerGrain 需要周期性扫描超时
- **THEN** SHALL 注册 Orleans reminder（持久化于 `OrleansRemindersTable`）
- **AND** SHALL NOT 使用 grain timer（grain timer 不跨去活）

#### Scenario: 跨重启检测孤儿 work

- **WHEN** server 与 runner 同步重启，某 outstanding work 在重启前未上报结果
- **THEN** reminder SHALL 在 silo 恢复后重新激活持有该 work 的 RunnerGrain
- **AND** 激活时 hydrate 的 outstanding 集 SHALL 包含该孤儿 work
- **AND** reminder tick SHALL 检测到该 work 超时并合成 `failed(reason=timeout)`

#### Scenario: 扫描零 DB 读

- **WHEN** reminder tick 触发
- **THEN** SHALL 只遍历内存 active 集判断超时
- **AND** SHALL NOT 在 tick 内执行 DB 读

#### Scenario: Reentrant 下扫描与合成的一致性

- **WHEN** RunnerGrain 标记为 `[Reentrant]` 且扫描期间并发发生 report
- **THEN** 扫描 SHALL 对内存 active 集 key 做 snapshot
- **AND** 合成前 SHALL 再次确认条目仍在（report 的 update 是权威）
- **AND** 已被 report 移除的 work SHALL NOT 被合成

#### Scenario: reminder 仅在 outstanding work 出现时注册一次

- **WHEN** RunnerGrain 通过 `PollOneWorkflowAsync` 或 `AssignAgentJobAsync` 取用 work，且当前不存在 `work-timeout` reminder
- **THEN** SHALL 以 register-if-absent 语义注册该 reminder（先 `GetReminder` 确认不存在再注册）
- **AND** SHALL NOT 使用会重置 due-time 的 `RegisterOrUpdateReminder` 来"确保"reminder 存在

#### Scenario: 后续 work 分配不重置 reminder due-time

- **WHEN** 一个 work 被取用时 `work-timeout` reminder 已存在（仍有 outstanding work）
- **THEN** 取用路径 SHALL NOT 调用 `RegisterOrUpdateReminder`（或任何会重置 due-time 的操作）
- **AND** 该 reminder 的下一次 fire 时刻 SHALL NOT 因新分配而被推迟
- **AND** 测试 SHALL 断言 reminder 调度行为（register-once vs. re-register）而非仅直接调用 `CheckWorkTimeoutsAsync`

#### Scenario: 老 outstanding work 不被新分配推迟超时判定

- **WHEN** runner 上已有一条 outstanding work W1 接近其 `TakenAt + WorkCompletionTimeout` deadline，且此时另一条新 work W2 被分配（在 W1 deadline 到达之前）
- **THEN** W1 的下一次超时扫描时刻 SHALL NOT 因 W2 的分配而被推迟
- **AND** 下一个稳定 scan tick SHALL 仍按 W1 自身的 `TakenAt` 判定
- **AND** 当 `now - W1.TakenAt > WorkCompletionTimeout` 时 SHALL 经 `ReportWorkflowResultAsync` 合成 `WorkResult(status="failed", reason="timeout")`

#### Scenario: reminder 仅在 outstanding work 清空时注销

- **WHEN** 一次 `work-timeout` 扫描观察到无 pending 或 running work（所有 work 已 report 或被合成终态）
- **THEN** SHALL 注销/停止该 reminder（保留现有 drain 行为）
- **AND** SHALL NOT 在仍有 outstanding work 时提前注销

#### Scenario: drain 后新 work 重新注册 reminder

- **WHEN** reminder 因 outstanding work 清空被注销后，又有新 work 被取用（outstanding 从零变为非零）
- **THEN** SHALL 重新注册 `work-timeout` reminder（register-if-absent）
- **AND** 该 reminder SHALL 从新注册时刻起按稳定周期 fire，覆盖新 work 的 deadline 判定

### Requirement: 超时相关时间读取统一走 TimeProvider

RunnerGrain SHALL 注入 `TimeProvider`（仓库已注册 `TimeProvider.System` 单例），所有超时相关的时间读取——`TakenAt`、扫描 `now`、`FinishedAt`——SHALL 经该 `TimeProvider` 读取，SHALL NOT 直接使用 `DateTime(Utc)Now`。取用点（`PollOneWorkflowAsync`、`AssignAgentJobAsync`）现有的 `UtcNow` SHALL 切换到 `TimeProvider`。`RecoverActiveWorkflowWorkAsync` SHALL 从持久台账 reload 原始 `TakenAt`，SHALL NOT 用 `UtcNow` 重置时钟。

#### Scenario: 取用点经 TimeProvider 记 TakenAt

- **WHEN** RunnerGrain 取用一个 work
- **THEN** `TakenAt` SHALL 由注入的 `TimeProvider.GetUtcNow()` 产生
- **AND** SHALL NOT 直接调用 `DateTimeOffset.UtcNow`

#### Scenario: 扫描 now 经 TimeProvider

- **WHEN** reminder tick 判断 `now - TakenAt > WorkCompletionTimeout`
- **THEN** `now` SHALL 由 `TimeProvider.GetUtcNow()` 产生

#### Scenario: RecoverActiveWorkflowWorkAsync 不重置时钟

- **WHEN** `RecoverActiveWorkflowWorkAsync` 恢复一个 active work
- **THEN** SHALL 从 `RunnerWorks` 台账 reload 原始 `TakenAt`
- **AND** SHALL NOT 用 `UtcNow` 覆盖 `TakenAt`

#### Scenario: 测试用 FakeTimeProvider 确定性验证

- **WHEN** 测试超时行为
- **THEN** SHALL 使用 `FakeTimeProvider` / `FixedTimeProvider` 推进时间
- **AND** SHALL 确定性断言超时合成 / 不合成
