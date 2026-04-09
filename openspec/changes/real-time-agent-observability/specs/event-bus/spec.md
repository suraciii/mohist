## MODIFIED Requirements

### Requirement: SSE 端点推送所有 agent 生命周期事件

SSE 端点 SHALL 推送所有 agent 生命周期事件。`ALL_EVENT_TYPES` 数组 SHALL 在现有事件基础上新增以下事件类型：`agent_text_chunk`、`main_tool_call`、`coder_text_chunk`、`coder_tool_call`、`ralph_task_update`、`ralph_loop_progress`。

#### Scenario: agent 暂停时 SSE 客户端收到通知
- **WHEN** AgentRunnerService 暂停一个 issue 的 agent session（gate 审批点）
- **AND** SSE 客户端已连接并订阅了该项目的 event
- **THEN** SSE 客户端收到 `event: agent_paused` 消息，payload 包含 `issueId` 和 `projectId`

#### Scenario: Main Agent 思考文本通过 SSE 推送
- **WHEN** Main Agent 的 LLM 输出文本
- **THEN** SSE 客户端收到 `event: agent_text_chunk` 消息，payload 包含 `{ text, stepIndex }`

#### Scenario: coder agent 文本通过 SSE 推送
- **WHEN** spawn_coder 或 ralph task 内部的 agent 输出文本
- **THEN** SSE 客户端收到 `event: coder_text_chunk` 消息，payload 包含 `{ executionId, acpSessionId, text }`

#### Scenario: ralph task 进度通过 SSE 推送
- **WHEN** ralph loop 的 task 状态变化
- **THEN** SSE 客户端收到 `event: ralph_task_update` 消息，payload 包含 `{ executionId, taskId, status, taskIndex, totalTasks }`
