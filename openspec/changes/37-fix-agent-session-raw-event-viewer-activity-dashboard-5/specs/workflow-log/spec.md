## MODIFIED Requirements

### Requirement: workflow_log 表存储执行事件

Server SHALL 维护 `workflow_log` 表，记录每次 spawn_coder 执行过程中的 ACP 事件。每条记录包含 issue_id、event_type 和完整 JSON data payload。

**Note:** Backend title derivation at `workflowLogRepo.insert()` time is deferred (P2). Frontend `reconstructRoundsFromLogs` derives titles from rawInput instead (see `tool-call-context-display` spec). No backend changes are required for this change.

#### Scenario: 记录 tool_call 事件
- **WHEN** opencode acp 子进程报告一个 tool_call sessionUpdate
- **THEN** 一条记录插入 workflow_log，event_type 为 'tool_call'，data 包含 tool name、input、status、locations 等完整信息

#### Scenario: 记录 agent_message_chunk 事件
- **WHEN** opencode acp 子进程报告一个 agent_message_chunk sessionUpdate
- **THEN** 一条记录插入 workflow_log，event_type 为 'agent_message_chunk'，data 包含 text content

#### Scenario: 记录执行结束信息
- **WHEN** spawn_coder 执行完成（成功或超时）
- **THEN** workflow_log 中包含最终的 stopReason 和 usage 信息
