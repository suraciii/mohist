## MODIFIED Requirements

### Requirement: spawn_coder 捕获所有 ACP 事件

spawn_coder 工具 SHALL 捕获 opencode acp 子进程的所有 sessionUpdate 事件类型，持久化到 workflow_log 表。`agent_thought_chunk` 事件 SHALL 被记录到 workflow_log 但 SHALL NOT 被累积到 `agentText`。

#### Scenario: 完整事件捕获
- **WHEN** spawn_coder 执行一次 oneshot session
- **THEN** 所有 sessionUpdate 事件（agent_message_chunk、tool_call、tool_call_update、plan、usage_update、agent_thought_chunk 等）都被记录到 workflow_log
- **AND** 返回给 Main Agent 的文本结果格式不变（仍为截断后的 agentText）

#### Scenario: 事件关联 issue
- **WHEN** spawn_coder 捕获到一个 ACP 事件
- **THEN** workflow_log 记录包含对应的 issue_id
- **AND** 包含 ACP session_id（如有）

#### Scenario: agent_thought_chunk 不污染 agentText
- **WHEN** opencode acp 报告 `agent_thought_chunk` 事件
- **THEN** 该事件 SHALL 被记录到 workflow_log（与其他事件一样）
- **AND** 该事件的文本内容 SHALL NOT 被追加到 `agentText`
- **AND** `agentText` 仅包含来自 `agent_message_chunk` 的内容
