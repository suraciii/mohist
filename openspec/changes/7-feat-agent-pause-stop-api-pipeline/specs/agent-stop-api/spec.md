## ADDED Requirements

### Requirement: Stop API 终止运行中的 agent

Server SHALL 提供 `POST /api/issues/:number/stop` 端点，强制终止指定 issue 正在运行的 ACP session（SIGTERM → SIGKILL），清理 AgentRunnerService 中的 activeAgents 条目和 pendingGates 条目，并将 issue status 设为 `blocked`。

#### Scenario: 成功停止运行中的 agent
- **WHEN** CLI 请求 `POST /api/issues/:number/stop`
- **AND** 该 issue 有 agent 正在运行（`agentRunner.isRunning(issueId)` 为 true）
- **THEN** server 向 ACP session 发送 cancel 信号
- **AND** 等待子进程退出（最长 10 秒）
- **AND** 从 `activeAgents` Map 中移除该 issue
- **AND** 从 `pendingGates` Map 中移除该 issue
- **AND** issue status 更新为 `blocked`
- **AND** 返回 200 `{ success: true, data: { message: "Agent stopped for issue #N" } }`

#### Scenario: 停止不存在的 issue
- **WHEN** CLI 请求 `POST /api/issues/:number/stop`
- **AND** issue 不存在
- **THEN** 返回 404

#### Scenario: 停止没有运行 agent 的 issue
- **WHEN** CLI 请求 `POST /api/issues/:number/stop`
- **AND** issue 存在但无 agent 运行
- **THEN** 返回 409 `{ success: false, error: "No agent running for issue #N" }`

#### Scenario: 无 project 上下文时拒绝停止
- **WHEN** CLI 请求 `POST /api/issues/:number/stop`
- **AND** server 无当前 project 上下文
- **THEN** 返回 400

### Requirement: Force 参数自动停止 agent 后执行操作

`POST /api/issues/:number/close`、`POST /api/issues/:number/reopen`、`POST /api/issues/:number/approve`、`POST /api/issues/:number/reject` 端点 SHALL 支持 `force` query parameter。当 `force=true` 且有 agent 运行时，先执行 stop（同 `POST /api/issues/:number/stop` 的完整流程），再执行原操作。

#### Scenario: Force close 运行中的 issue
- **WHEN** CLI 请求 `POST /api/issues/:number/close?force=true`
- **AND** issue 有 agent 正在运行
- **THEN** server 先停止 agent（cancel + cleanup）
- **AND** 然后正常执行 close 操作
- **AND** 返回 200

#### Scenario: Force reopen 运行中的 issue
- **WHEN** CLI 请求 `POST /api/issues/:number/reopen?force=true`
- **AND** issue 有 agent 正在运行
- **THEN** server 先停止 agent
- **AND** 然后正常执行 reopen 操作
- **AND** 返回 200

#### Scenario: Force approve 运行中的 issue
- **WHEN** CLI 请求 `POST /api/issues/:number/approve?force=true`
- **AND** issue 有 agent 正在运行
- **THEN** server 先停止 agent
- **AND** 然后正常执行 approve 操作
- **AND** 返回 200

#### Scenario: Force reject 运行中的 issue
- **WHEN** CLI 请求 `POST /api/issues/:number/reject?force=true`
- **AND** issue 有 agent 正在运行
- **THEN** server 先停止 agent
- **AND** 然后正常执行 reject 操作
- **AND** 返回 200

#### Scenario: 无 force 参数保持原有 409 行为
- **WHEN** CLI 请求 `POST /api/issues/:number/close`（无 force 参数）
- **AND** issue 有 agent 正在运行
- **THEN** 返回 409，行为与当前一致
