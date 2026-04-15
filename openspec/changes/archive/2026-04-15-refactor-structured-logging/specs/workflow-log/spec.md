## MODIFIED Requirements

### Requirement: workflow_log 表存储执行事件

Server SHALL 维护 `workflow_log` 表，记录每次 spawn_coder 执行过程中的 ACP 事件。每条记录包含 issue_id、event_type 和完整 JSON data payload。同时，所有 workflow 状态转换 SHALL 通过 `Log` 模块记录技术日志。

#### Scenario: 记录 tool_call 事件
- **WHEN** opencode acp 子进程报告一个 tool_call sessionUpdate
- **THEN** 一条记录插入 workflow_log，event_type 为 'tool_call'，data 包含 tool name、input、status、locations 等完整信息
- **AND** 通过 `Log` 记录一条 INFO 级别技术日志，包含 `service=spawn-coder eventType=tool_call issueNumber=<n>`

#### Scenario: 记录 agent_message_chunk 事件
- **WHEN** opencode acp 子进程报告一个 agent_message_chunk sessionUpdate
- **THEN** 一条记录插入 workflow_log，event_type 为 'agent_message_chunk'，data 包含 text content
- **AND** 通过 `Log` 记录一条 DEBUG 级别技术日志

#### Scenario: 记录执行结束信息
- **WHEN** spawn_coder 执行完成（成功或超时）
- **THEN** workflow_log 中包含最终的 stopReason 和 usage 信息
- **AND** 通过 `Log` 记录一条 INFO 级别日志，包含 `status=completed duration=<ms> stopReason=<reason>`

#### Scenario: workflow 状态转换记录技术日志
- **WHEN** Issue 的 stage 或 status 发生变化
- **THEN** 通过 `Log` 记录一条 INFO 级别日志，包含 `service=workflow issueNumber=<n> fromStage=<a> toStage=<b>`
