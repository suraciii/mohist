## ADDED Requirements

### Requirement: EventBus 支持冲突解决生命周期事件

EventBus SHALL 支持以下冲突解决事件类型：

- `agent_conflict_resolution_started`: payload `{ issueId, projectId, issueNumber, conflictFiles }`
- `agent_conflict_resolution_completed`: payload `{ issueId, projectId, issueNumber }`
- `agent_conflict_resolution_failed`: payload `{ issueId, projectId, issueNumber, error }`

这些事件 SHALL 包含在 `ALL_EVENT_TYPES` 中并通过 SSE 端点推送。

#### Scenario: 冲突解决开始时 SSE 客户端收到通知

- **WHEN** MergeQueue 调用 `resolveConflicts` delegate 启动 agent 冲突解决 session
- **AND** SSE 客户端已连接并订阅了该项目的 event
- **THEN** SSE 客户端收到 `event: agent_conflict_resolution_started` 消息
- **AND** payload 包含 `issueId`、`projectId`、`issueNumber`、`conflictFiles`

#### Scenario: 冲突解决完成时 SSE 客户端收到通知

- **WHEN** agent 成功解决所有冲突标记并完成 rebase continue
- **AND** SSE 客户端已连接
- **THEN** SSE 客户端收到 `event: agent_conflict_resolution_completed` 消息
- **AND** payload 包含 `issueId`、`projectId`、`issueNumber`

#### Scenario: 冲突解决失败时 SSE 客户端收到通知

- **WHEN** agent 冲突解决 session 失败（超时、错误、无法解决）
- **AND** SSE 客户端已连接
- **THEN** SSE 客户端收到 `event: agent_conflict_resolution_failed` 消息
- **AND** payload 包含 `issueId`、`projectId`、`issueNumber`、`error`

#### Scenario: 冲突解决事件包含在 ALL_EVENT_TYPES 中

- **WHEN** 系统初始化 EventBus
- **THEN** `ALL_EVENT_TYPES` 数组包含 `agent_conflict_resolution_started`、`agent_conflict_resolution_completed`、`agent_conflict_resolution_failed`
