## MODIFIED Requirements

### Requirement: SSE 端点推送所有 agent 生命周期事件

SSE 端点 SHALL 推送所有 agent 生命周期事件，包括 `agent_paused`。`ALL_EVENT_TYPES` 数组 SHALL 包含 `agent_started`、`agent_completed`、`agent_paused`、`agent_error`、`approval_requested`、`question_asked`、`question_answered`、`rebase_started`、`rebase_completed`、`rebase_conflict`、`rebase_retry`、`merge_blocked`。

#### Scenario: agent 暂停时 SSE 客户端收到通知
- **WHEN** AgentRunnerService 暂停一个 issue 的 agent session（gate 审批点）
- **AND** SSE 客户端已连接并订阅了该项目的 event
- **THEN** SSE 客户端收到 `event: agent_paused` 消息，payload 包含 `issueId` 和 `projectId`

#### Scenario: rebase 开始时 SSE 客户端收到通知
- **WHEN** MergeQueue 开始对一个 issue 执行 rebase
- **AND** SSE 客户端已连接并订阅了该项目的 event
- **THEN** SSE 客户端收到 `event: rebase_started` 消息，payload 包含 `issueId`、`projectId`、`issueNumber`

#### Scenario: rebase 完成时 SSE 客户端收到通知
- **WHEN** MergeQueue 对一个 issue 的 rebase 成功完成
- **AND** SSE 客户端已连接并订阅了该项目的 event
- **THEN** SSE 客户端收到 `event: rebase_completed` 消息，payload 包含 `issueId`、`projectId`、`issueNumber`

#### Scenario: rebase 冲突时 SSE 客户端收到通知
- **WHEN** MergeQueue 对一个 issue 的 rebase 遇到冲突并 abort
- **AND** SSE 客户端已连接并订阅了该项目的 event
- **THEN** SSE 客户端收到 `event: rebase_conflict` 消息，payload 包含 `issueId`、`projectId`、`issueNumber`、`conflictingFiles`

#### Scenario: 自动重试 rebase 时 SSE 客户端收到通知
- **WHEN** 定时检查器检测到 master 有新 commit 并重新入队一个 blocked issue
- **AND** SSE 客户端已连接并订阅了该项目的 event
- **THEN** SSE 客户端收到 `event: rebase_retry` 消息，payload 包含 `issueId`、`projectId`、`issueNumber`、`attempt`

#### Scenario: issue 最终 blocked 时 SSE 客户端收到通知
- **WHEN** 一个 issue 的自动重试达到上限
- **AND** SSE 客户端已连接并订阅了该项目的 event
- **THEN** SSE 客户端收到 `event: merge_blocked` 消息，payload 包含 `issueId`、`projectId`、`issueNumber`、`retryCount`、`lastConflict`

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
