## ADDED Requirements

### Requirement: API 提供 retry 端点从断点恢复

Server SHALL 提供 `POST /api/issues/:number/retry` 端点，允许用户对 blocked issue 从断点恢复执行，保留已完成的 task 进度。

#### Scenario: Retry blocked issue

- **WHEN** 用户请求 `POST /api/issues/:number/retry`
- **AND** issue status 为 blocked
- **AND** issue 存在可恢复的进度（如 tasks.json 中有部分 task 已完成）
- **THEN** issue status 改为 active
- **THEN** retryCount 重置为 0
- **THEN** blockedReason 被清除
- **THEN** 系统 resume pipeline（保留已完成 task，从中断处继续）
- **THEN** 返回 200，message 包含 "retrying from checkpoint"

#### Scenario: Retry 非 blocked issue 被拒绝

- **WHEN** 用户请求 `POST /api/issues/:number/retry`
- **AND** issue status 不为 blocked
- **THEN** 返回 409 Conflict
- **AND** 错误信息 "Issue is not blocked"

#### Scenario: Retry 时 agent 已在运行被拒绝

- **WHEN** 用户请求 `POST /api/issues/:number/retry`
- **AND** issue 已有 agent 在运行
- **THEN** 返回 409 Conflict
- **AND** 错误信息 "Agent is already running for this issue"

#### Scenario: Retry 无可恢复进度时 fallback

- **WHEN** 用户请求 `POST /api/issues/:number/retry`
- **AND** issue status 为 blocked
- **AND** 无可恢复的进度（无 tasks.json 或 worktree）
- **THEN** 系统将 issue stage 重置为 Draft
- **THEN** issue status 改为 active
- **THEN** 返回 200，message 包含 "no checkpoint found, reset to draft"

### Requirement: API 提供 restart 端点重新开始

Server SHALL 提供 `POST /api/issues/:number/restart` 端点，允许用户对 blocked issue 丢弃所有进度，从头开始。

#### Scenario: Restart blocked issue

- **WHEN** 用户请求 `POST /api/issues/:number/restart`
- **AND** issue status 为 blocked
- **THEN** issue stage 重置为 Draft
- **THEN** issue status 改为 active
- **THEN** retryCount 重置为 0
- **THEN** blockedReason 被清除
- **THEN** approvalState 被清除
- **THEN** 返回 200，message 包含 "reset to draft, use start to begin again"

#### Scenario: Restart 非 blocked issue 被拒绝

- **WHEN** 用户请求 `POST /api/issues/:number/restart`
- **AND** issue status 不为 blocked
- **THEN** 返回 409 Conflict
- **AND** 错误信息 "Issue is not blocked"

#### Scenario: Restart 时 agent 已在运行被拒绝

- **WHEN** 用户请求 `POST /api/issues/:number/restart`
- **AND** issue 已有 agent 在运行
- **THEN** 返回 409 Conflict
- **AND** 错误信息 "Agent is already running for this issue"

### Requirement: Agent status API 暴露 blocked issues

`GET /api/agent/status` 返回值 SHALL 包含 `blockedIssues` 数组，列出所有 status 为 blocked 的 issue。

#### Scenario: 返回 blocked issues 列表

- **WHEN** 请求 `GET /api/agent/status`
- **THEN** 返回 `blockedIssues` 数组
- **AND** 每个条目包含 `{ issueNumber, stage, blockedReason, retryCount }`

#### Scenario: 无 blocked issues

- **WHEN** 请求 `GET /api/agent/status`
- **AND** 没有 status 为 blocked 的 issue
- **THEN** `blockedIssues` 为空数组

### Requirement: agent_blocked 事件通过 SSE 推送

当 issue 进入 blocked 状态时，系统 SHALL 通过 EventBus emit `agent_blocked` 事件，Web UI 可通过 SSE 实时感知。

#### Scenario: Blocked 事件推送

- **WHEN** issue 被标记为 blocked
- **THEN** EventBus emit `agent_blocked` 事件
- **AND** payload 包含 `{ issueId, projectId, issueNumber, blockedReason, retryCount }`

#### Scenario: Web UI 通过 SSE 接收 blocked 事件

- **WHEN** Web UI SSE 连接收到 `agent_blocked` 事件
- **THEN** IssueDetailPage 自动刷新，展示 blocked reason 和操作按钮
