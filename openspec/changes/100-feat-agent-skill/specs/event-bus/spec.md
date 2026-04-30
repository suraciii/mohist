## MODIFIED Requirements

### Requirement: SSE 端点推送所有 agent 生命周期事件

SSE 端点 SHALL 推送所有 agent 生命周期事件，包括 `agent_paused`。`ALL_EVENT_TYPES` 数组 SHALL 包含 `agent_started`、`agent_completed`、`agent_paused`、`agent_error`、`approval_requested`、`question_asked`、`question_answered`、`schedule_triggered`、`schedule_completed`、`schedule_failed`。

#### Scenario: agent 暂停时 SSE 客户端收到通知

- **WHEN** AgentRunnerService 暂停一个 issue 的 agent session（gate 审批点）
- **AND** SSE 客户端已连接并订阅了该项目的 event
- **THEN** SSE 客户端收到 `event: agent_paused` 消息，payload 包含 `issueId` 和 `projectId`

#### Scenario: 调度触发时 SSE 客户端收到通知

- **WHEN** SchedulerService 触发一个 skill 的定时执行
- **AND** SSE 客户端已连接并订阅了该项目的 event
- **THEN** SSE 客户端收到 `event: schedule_triggered` 消息，payload 包含 `skillId`、`skillName`、`scheduleType`

#### Scenario: 调度执行完成时 SSE 客户端收到通知

- **WHEN** 一个定时触发的 skill 执行完成
- **AND** SSE 客户端已连接并订阅了该项目的 event
- **THEN** SSE 客户端收到 `event: schedule_completed` 消息，payload 包含 `skillId`、`skillName`、`issueId`

#### Scenario: 调度执行失败时 SSE 客户端收到通知

- **WHEN** 一个定时触发的 skill 执行失败
- **AND** SSE 客户端已连接并订阅了该项目的 event
- **THEN** SSE 客户端收到 `event: schedule_failed` 消息，payload 包含 `skillId`、`skillName`、`error`
