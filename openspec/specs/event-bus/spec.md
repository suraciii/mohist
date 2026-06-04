# OpenSpec Capability: event-bus

### Requirement: SSE 端点推送所有 agent 生命周期事件

SSE 端点 SHALL 推送所有 agent 生命周期事件，包括 `agent_paused`。`ALL_EVENT_TYPES` 数组 SHALL 包含 `agent_started`、`agent_completed`、`agent_paused`、`agent_error`、`approval_requested`、`question_asked`、`question_answered`。

#### Scenario: agent 暂停时 SSE 客户端收到通知
- **WHEN** workflow runtime pauses an issue at an approval point
- **AND** SSE 客户端已连接并订阅了该项目的 event
- **THEN** SSE 客户端收到 `event: agent_paused` 消息，payload 包含 `issueId` 和 `projectId`

### Requirement: EventBus 支持 question 事件

EventBus SHALL 支持以下事件类型：

- `question_asked`: payload `{ issueId, projectId, questionId, question }`
- `question_answered`: payload `{ issueId, projectId, questionId, answer }`

#### Scenario: question_asked 事件推送
- **WHEN** ask_user 工具创建一个新问题
- **THEN** EventBus emit `question_asked` 事件
- **AND** SSE 客户端收到该事件

#### Scenario: question_answered 事件推送
- **WHEN** 用户通过 API 回复一个问题
- **THEN** EventBus emit `question_answered` 事件
- **AND** SSE 客户端收到该事件

### Requirement: SSE 连接心跳检测

SSE 端点 SHALL 每 30 秒发送一次心跳注释（`: heartbeat\n`），保持连接活跃并检测断开。如果 `stream.writeSSE` 写入失败（连接已断），SHALL 立即清理该连接的所有 event listener 并结束 stream。

#### Scenario: 正常连接收到心跳
- **WHEN** SSE 客户端已连接 30 秒
- **THEN** 客户端收到 `: heartbeat\n` 注释
- **AND** 客户端忽略该注释（SSE 规范行为）

#### Scenario: 连接断开后清理 listener
- **WHEN** SSE 客户端异常断开（进程崩溃、网络中断）
- **AND** server 尝试发送心跳或事件时检测到写入失败
- **THEN** 该连接的所有 event listener 被清理
- **AND** stream 结束
- **AND** EventBus 的 listener Map 中不再包含该连接的 handler

### Requirement: REQ-BDA-EVENTS-001 Drift lifecycle emits typed events

Mohist SHALL emit typed events for base advancement, drift detection, rebase opportunity decisions, safe-window transitions, evidence invalidation, and user attention requests so live clients can refresh state.

#### Scenario: Base advancement event is emitted

- **WHEN** Integrate successfully advances the project base branch
- **THEN** Mohist SHALL emit an event containing project, issue, base branch, and new base position facts

#### Scenario: Drift opportunity events are emitted

- **WHEN** an active candidate is evaluated after base advancement
- **THEN** Mohist SHALL emit events for drift detection, opportunity opening, decision made, and user attention when applicable

#### Scenario: Protected work and safe window events are emitted

- **WHEN** rebase is deferred because mutating work is active
- **THEN** Mohist SHALL emit an active-work-protected event
- **AND** when the issue reaches a safe window, Mohist SHALL emit a safe-rebase-window event

#### Scenario: Evidence invalidation event is emitted

- **WHEN** base drift or rebase invalidates candidate evidence
- **THEN** Mohist SHALL emit an event that identifies affected evidence and issue context
