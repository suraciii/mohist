## ADDED Requirements

### Requirement: AgentRunnerService 暴露 stop 方法

AgentRunnerService SHALL 暴露 `stop(issueId: string)` 方法，用于外部停止指定 issue 的运行中 agent。该方法 SHALL：1) 通过 ACP connection.cancel 终止 session，2) 等待子进程退出（最长 10 秒，超时 SIGKILL），3) 从 `activeAgents` Map 移除条目，4) 从 `pendingGates` Map 移除条目，5) 将 issue status 更新为 `blocked`，6) emit `agent_stopped` 事件。

#### Scenario: 停止运行中的 agent
- **WHEN** `stop(issueId)` 被调用
- **AND** `activeAgents` 中存在该 issueId
- **THEN** ACP session 被 cancel
- **AND** 子进程被 SIGTERM（10 秒后 SIGKILL）
- **AND** `activeAgents` 移除该条目
- **AND** `pendingGates` 移除该条目
- **AND** issue status 更新为 `blocked`
- **AND** EventBus emit `agent_stopped` 事件（payload 包含 issueId, projectId, issueNumber）

#### Scenario: 停止不存在的 agent
- **WHEN** `stop(issueId)` 被调用
- **AND** `activeAgents` 中不存在该 issueId
- **THEN** 方法 SHALL 返回 false（表示无 agent 需要停止）

#### Scenario: Stop 在 agent 完成前到达
- **WHEN** `stop(issueId)` 被调用
- **AND** agent 恰好在 stop 过程中自行完成
- **THEN** stop 方法 SHALL 安全处理竞态（不抛异常，返回 true 表示已清理）
