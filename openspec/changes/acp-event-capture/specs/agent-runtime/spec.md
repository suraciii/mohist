## MODIFIED Requirements

### Requirement: spawn_coder 捕获所有 ACP 事件

spawn_coder 工具 SHALL 捕获 opencode acp 子进程的所有 sessionUpdate 事件类型，持久化到 workflow_log 表。

#### Scenario: 完整事件捕获
- **WHEN** spawn_coder 执行一次 oneshot session
- **THEN** 所有 sessionUpdate 事件（agent_message_chunk、tool_call、tool_call_update、plan、usage_update、agent_thought_chunk 等）都被记录到 workflow_log
- **AND** 返回给 Main Agent 的文本结果格式不变（仍为截断后的 agentText）

#### Scenario: 事件关联 issue
- **WHEN** spawn_coder 捕获到一个 ACP 事件
- **THEN** workflow_log 记录包含对应的 issue_id
- **AND** 包含 ACP session_id（如有）

### Requirement: spawn_coder 通过 EventBus 推送 action 事件

spawn_coder 工具 SHALL 在捕获到关键 ACP 事件时通过 EventBus emit，使 Web UI 和 mo attach 可以实时感知 agent 动作。

#### Scenario: 推送 tool_call 事件
- **WHEN** opencode acp 报告 tool_call 事件
- **THEN** EventBus emit `tool_call` 事件，payload 包含 issueId、projectId、tool name、status、file locations

#### Scenario: 不推送高频事件
- **WHEN** opencode acp 报告 agent_message_chunk 事件
- **THEN** 不通过 EventBus emit（仅存入 workflow_log）
