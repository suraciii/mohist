## MODIFIED Requirements

### Requirement: SSE 端点推送所有 agent 生命周期事件

SSE 端点 SHALL 推送所有 agent 生命周期事件，包括 `agent_paused`。`ALL_EVENT_TYPES` 数组 SHALL 包含 `agent_started`、`agent_completed`、`agent_paused`、`agent_error`、`approval_requested`、`question_asked`、`question_answered`、`rebase_started`、`rebase_progress`、`rebase_completed`、`rebase_conflict`。

#### Scenario: agent 暂停时 SSE 客户端收到通知
- **WHEN** AgentRunnerService 暂停一个 issue 的 agent session（gate 审批点）
- **AND** SSE 客户端已连接并订阅了该项目的 event
- **THEN** SSE 客户端收到 `event: agent_paused` 消息，payload 包含 `issueId` 和 `projectId`

#### Scenario: rebase 开始时 SSE 推送
- **WHEN** 用户触发 `POST /api/issues/:number/rebase`
- **AND** 前置条件检查通过
- **THEN** SSE 客户端收到 `event: rebase_started` 消息，payload 包含 `{ issueNumber, projectId }`

#### Scenario: rebase 进度 SSE 推送
- **WHEN** rebase 操作进入不同阶段
- **THEN** SSE 客户端收到 `event: rebase_progress` 消息，payload 包含 `{ issueNumber, step }`，step 取值为 `"fetching"` | `"rebasing"` | `"verifying"`

#### Scenario: rebase 完成 SSE 推送
- **WHEN** rebase 操作成功完成
- **THEN** SSE 客户端收到 `event: rebase_completed` 消息，payload 包含 `{ issueNumber, projectId, rebased }`

#### Scenario: rebase 冲突 SSE 推送
- **WHEN** rebase 操作因冲突中止
- **THEN** SSE 客户端收到 `event: rebase_conflict` 消息，payload 包含 `{ issueNumber, conflicts: string[] }`
