## ADDED Requirements

### Requirement: Agent Runner 可以 spawn opencode agents

Server SHALL 能够 spawn opencode agents 执行任务。

#### Scenario: spawn designer agent
- **WHEN** Issue 进入 designing 阶段
- **THEN** server 执行 `child_process.spawn("opencode", ...)`
- **AND** agent 接收 Issue 信息作为输入
- **AND** agent 负责生成设计文档并创建 PR

#### Scenario: spawn implementer agent
- **WHEN** Issue 进入 implementing 阶段
- **THEN** server 执行 `child_process.spawn("opencode", ...)`
- **AND** agent 接收设计文档和 Issue 信息
- **AND** agent 负责实现代码并更新 PR

### Requirement: Agent Runner 监控 agent 执行状态

Server SHALL 监控运行中的 agents。

#### Scenario: agent 正常完成
- **WHEN** agent 进程退出码为 0
- **THEN** server 认为任务成功
- **AND** 触发下一阶段

#### Scenario: agent 执行失败
- **WHEN** agent 进程退出码非 0
- **THEN** server 认为任务失败
- **AND** Issue 状态变为 blocked
- **AND** 记录错误日志

#### Scenario: agent 超时
- **WHEN** agent 运行超过 30 分钟
- **THEN** server 终止 agent 进程
- **AND** Issue 状态变为 blocked

### Requirement: Agent Runner 管理并发

Server SHALL 限制同时运行的 agent 数量。

#### Scenario: 达到并发上限
- **WHEN** 已有 8 个 agent 运行
- **THEN** 新任务保持等待
- **AND** 当有 agent 完成时，按队列顺序启动下一个
