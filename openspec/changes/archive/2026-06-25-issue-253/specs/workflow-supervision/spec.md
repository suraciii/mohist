## ADDED Requirements

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

work 执行超时（卡死 / runaway）SHALL 由 runner 进程判定，因为只有 runner 进程拥有进度信号（token 流、子进程存活）。runner 进程 SHALL 在到点时上报 `failed`。

#### Scenario: 卡死工作由 runner 进程判失败

- **WHEN** 一个工作在 runner 进程中表现为卡死（超过 liveness quiet 阈值）
- **THEN** runner 进程 SHALL 通过 probe 检测并上报 `failed`
- **AND** WorkflowGrain SHALL NOT 自行基于墙钟判定超时

#### Scenario: maxDuration 与 quiet 阈值分离

- **WHEN** runner 进程配置 liveness
- **THEN** maxDuration 帽（抓 runaway）SHALL 与 quiet 阈值（抓卡死）分离
- **AND** 两者 SHALL NOT 在同一时刻同时触发以致互相无冗余
- **AND** maxDuration SHALL NOT 被设成与 grain 旧值相同的 20 分钟

### Requirement: runner 丢失由 RunnerGrain 兜底合成失败

runner 丢失（心跳失联）SHALL 由 RunnerGrain 检测。RunnerGrain SHALL 持有 outstanding-work 集（镜像现有 `_agentJobs` 记账，覆盖 workflow work）。检测到 runner 丢失时，RunnerGrain SHALL 遍历 outstanding 集，经正常 report 通道（`ReportWorkflowResultAsync`）合成 `failed` 上报。

#### Scenario: RunnerGrain 记账 outstanding work

- **WHEN** 一个 workflow work 被分配执行
- **THEN** RunnerGrain SHALL 在其 outstanding-work 集中记账该 work
- **AND** 当前 `ReportWorkflowResultAsync` 作为纯 relay 不记账的行为 SHALL 被补齐

#### Scenario: runner 丢失触发合成失败

- **WHEN** RunnerGrain 检测到一个 runner 丢失（心跳失联）
- **THEN** RunnerGrain SHALL 遍历该 runner 的 outstanding-work 集
- **AND** SHALL 经正常 report 通道为每个 outstanding work 合成 `failed` 上报
- **AND** WorkflowGrain 收到的 SHALL 与普通失败无异的 work 结果

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
