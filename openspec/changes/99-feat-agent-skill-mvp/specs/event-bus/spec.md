## MODIFIED Requirements

### Requirement: SSE 端点推送 skill 生命周期事件

`ALL_EVENT_TYPES` 数组 SHALL 新增 `skill_started`、`skill_completed`、`skill_failed` 三个事件类型，使 SSE 客户端能接收到 skill 执行的生命周期事件。

#### Scenario: skill 开始执行时 SSE 客户端收到通知
- **WHEN** skill 执行被触发
- **AND** SSE 客户端已连接并订阅了该项目的 event
- **THEN** SSE 客户端收到 `event: skill_started` 消息，payload 包含 `skillName`、`runId`、`projectId`

#### Scenario: skill 完成时 SSE 客户端收到通知
- **WHEN** skill 执行完成（成功或失败）
- **AND** SSE 客户端已连接并订阅了该项目的 event
- **THEN** SSE 客户端收到 `event: skill_completed` 或 `event: skill_failed` 消息，payload 包含 `skillName`、`runId`、`projectId`、`issueId`（如已创建）

## ADDED Requirements

### Requirement: EventBus 支持 skill 生命周期事件

EventBus SHALL 支持以下 skill 事件类型：

- `skill_started`: payload `{ skillName: string; runId: string; projectId: string }`
- `skill_completed`: payload `{ skillName: string; runId: string; projectId: string; issueId?: string }`
- `skill_failed`: payload `{ skillName: string; runId: string; projectId: string; error: string }`

#### Scenario: skill_started 事件推送
- **WHEN** skill 执行被触发
- **THEN** EventBus emit `skill_started` 事件
- **AND** SSE 客户端收到该事件

#### Scenario: skill_completed 事件推送
- **WHEN** skill ACP session 成功完成
- **THEN** EventBus emit `skill_completed` 事件
- **AND** payload 包含创建的 issueId（如有）

#### Scenario: skill_failed 事件推送
- **WHEN** skill 执行失败（ACP 错误或超时）
- **THEN** EventBus emit `skill_failed` 事件
- **AND** payload 包含 error 描述
