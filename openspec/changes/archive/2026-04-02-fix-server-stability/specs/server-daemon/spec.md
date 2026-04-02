## MODIFIED Requirements

### Requirement: Server 管理 Issue pipeline

Server SHALL 管理 Issue pipeline 的调度和执行。

#### Scenario: Issue 启动
- **WHEN** 用户启动一个 Issue 处理
- **THEN** Issue stage 从 `draft` 变为 `plan`
- **AND** Agent Runtime 为该 Issue 创建 Main Agent session

#### Scenario: 并发限制
- **WHEN** 已有 maxConcurrentAgents 个 agent 运行
- **AND** 新 Issue 被启动
- **THEN** 新 Issue 保持 `plan` stage 但等待 agent 可用
- **AND** 当有 agent 完成时，下一个等待的 Issue 开始执行

#### Scenario: 关闭有运行中 agent 的 issue
- **WHEN** 用户请求关闭一个 issue
- **AND** 该 issue 有正在运行的 agent
- **THEN** server 返回 409 Conflict
- **AND** 错误信息包含 "agent is running" 及解决方案提示

#### Scenario: 关闭无运行中 agent 的 issue
- **WHEN** 用户请求关闭一个 issue
- **AND** 该 issue 没有运行中的 agent
- **THEN** server 将 issue status 设为 `blocked`
- **AND** 返回成功响应
