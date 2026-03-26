## MODIFIED Requirements

### Requirement: Agent Runner 可以 spawn opencode agents

Server SHALL 能够 spawn opencode agents 在对应 Issue 的 worktree 中执行任务。

#### Scenario: spawn designer agent

- **WHEN** Issue 进入 designing 阶段
- **AND** WorkflowEngine 分发该任务给 Worker
- **THEN** Worker 执行 `child_process.spawn("opencode", ...)` 
- **AND** agent 的 cwd 为该 Issue 的 worktree 路径（`~/.mohist/projects/{projectName}/worktrees/issue-{N}/`）
- **AND** agent 接收 Issue 信息作为输入
- **AND** agent 负责生成设计文档

#### Scenario: spawn implementer agent

- **WHEN** Issue 进入 implementing 阶段
- **AND** WorkflowEngine 分发该任务给 Worker
- **THEN** Worker 执行 `child_process.spawn("opencode", ...)`
- **AND** agent 的 cwd 为该 Issue 的 worktree 路径
- **AND** agent 接收设计文档和 Issue 信息
- **AND** agent 负责实现代码

### Requirement: Agent Runner 监控 agent 执行状态

Server SHALL 监控运行中的 agents。

#### Scenario: agent 正常完成

- **WHEN** agent 进程退出码为 0
- **THEN** WorkflowEngine 将 Task 标记为 completed
- **AND** 触发 Issue 阶段流转

#### Scenario: agent 执行失败

- **WHEN** agent 进程退出码非 0
- **THEN** WorkflowEngine 将 Task 标记为 failed 并记录错误
- **AND** Issue 状态变为 blocked
- **AND** 记录错误日志到 `~/.mohist/projects/{projectName}/logs/issue-{N}/`

#### Scenario: agent 超时

- **WHEN** agent 运行超过 30 分钟
- **THEN** WorkflowEngine 终止 agent 进程
- **AND** Task 标记为 failed
- **AND** Issue 状态变为 blocked

## REMOVED Requirements

### Requirement: Agent Runner 管理并发

**Reason**: 并发管理职责从 AgentRunner 移交给 WorkflowEngine 的多 Worker 模式

**Migration**: WorkflowEngine 通过 `maxConcurrentAgents`（默认 8）个独立 Worker 管理并发，见 `workflow-engine/spec.md`

## ADDED Requirements

### Requirement: Agent Runner 将日志写入 Issue 级别目录

Agent Runner SHALL 将每个 Agent 的 stdout/stderr 写入独立的日志文件。

#### Scenario: 日志写入

- **WHEN** Agent 执行中
- **THEN** stdout 和 stderr 实时写入 `~/.mohist/projects/{projectName}/logs/issue-{N}/agent-{stage}.log`
- **AND** 日志目录在 Agent 启动前自动创建

#### Scenario: 日志追加

- **WHEN** 同一 Issue 多次启动 Agent（如设计失败重试）
- **THEN** 新日志追加到已有日志文件，不覆盖
