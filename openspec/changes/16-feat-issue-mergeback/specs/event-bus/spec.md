## ADDED Requirements

### Requirement: EventBus 支持合并队列事件

EventBus SHALL 支持以下合并队列事件类型：

- `merge_queued`: payload `{ issueId, issueNumber, projectId }`
- `merge_started`: payload `{ issueId, issueNumber, projectId }`
- `merge_completed`: payload `{ issueId, issueNumber, projectId, message }`
- `merge_failed`: payload `{ issueId, issueNumber, projectId, reason, message }`

其中 `reason` 为 `conflict` 或 `build-failed`。

#### Scenario: issue 入队时 SSE 客户端收到 merge_queued

- **WHEN** MergeQueue 将 issue 入队
- **THEN** EventBus emit `merge_queued` 事件
- **AND** SSE 客户端收到 `event: merge_queued` 消息，payload 包含 `issueId`、`issueNumber`、`projectId`

#### Scenario: 合并开始时 SSE 客户端收到 merge_started

- **WHEN** MergeQueue 开始处理 issue 的合并
- **THEN** EventBus emit `merge_started` 事件
- **AND** SSE 客户端收到 `event: merge_started` 消息

#### Scenario: 合并成功时 SSE 客户端收到 merge_completed

- **WHEN** mergeBack 成功且构建验证通过
- **THEN** EventBus emit `merge_completed` 事件，payload 包含 `message`（如 "Merged mo/issue-1 into main"）
- **AND** SSE 客户端收到 `event: merge_completed` 消息

#### Scenario: 合并失败时 SSE 客户端收到 merge_failed

- **WHEN** mergeBack 失败或构建验证失败
- **THEN** EventBus emit `merge_failed` 事件，payload 包含 `reason` 和 `message`
- **AND** SSE 客户端收到 `event: merge_failed` 消息

## MODIFIED Requirements

### Requirement: SSE 端点推送所有 agent 生命周期事件

SSE 端点 SHALL 推送所有 agent 生命周期事件和合并队列事件。`ALL_EVENT_TYPES` 数组 SHALL 包含 `agent_started`、`agent_completed`、`agent_paused`、`agent_error`、`approval_requested`、`question_asked`、`question_answered`、`merge_queued`、`merge_started`、`merge_completed`、`merge_failed`。

#### Scenario: agent 暂停时 SSE 客户端收到通知
- **WHEN** AgentRunnerService 暂停一个 issue 的 agent session（gate 审批点）
- **AND** SSE 客户端已连接并订阅了该项目的 event
- **THEN** SSE 客户端收到 `event: agent_paused` 消息，payload 包含 `issueId` 和 `projectId`
