## MODIFIED Requirements

### Requirement: spawn_coder 捕获所有 ACP 事件

spawn_coder 工具 SHALL 捕获 opencode acp 子进程的所有 sessionUpdate 事件类型，持久化到 workflow_log 表。spawn 日志和 session log 中的 `taskId` 字段 SHALL 记录实际 task ID（如 `T-001`），而非 prompt 文本。

#### Scenario: 完整事件捕获
- **WHEN** spawn_coder 执行一次 oneshot session
- **THEN** 所有 sessionUpdate 事件（agent_message_chunk、tool_call、tool_call_update、plan、usage_update、agent_thought_chunk 等）都被记录到 workflow_log
- **AND** 返回给 Main Agent 的文本结果格式不变（仍为截断后的 agentText）

#### Scenario: 事件关联 issue
- **WHEN** spawn_coder 捕获到一个 ACP 事件
- **THEN** workflow_log 记录包含对应的 issue_id
- **AND** 包含 ACP session_id（如有）

#### Scenario: ACP session spawn 日志记录实际 task ID
- **WHEN** `AcpSessionOptions` 包含 `taskId` 字段
- **THEN** spawn 日志和 session log 中的 `taskId` 字段 SHALL 记录该 task ID 值（如 `T-001`）
- **AND** prompt 文本前 100 字符 SHALL 记录在 `promptPreview` 字段中（而非 `taskId`）

#### Scenario: ACP session spawn 无 taskId 时向后兼容
- **WHEN** `AcpSessionOptions` 未包含 `taskId` 字段
- **THEN** spawn 日志中的 `taskId` 字段 SHALL 为 undefined 或省略
- **AND** prompt 文本前 100 字符仍记录在 `promptPreview` 字段中
