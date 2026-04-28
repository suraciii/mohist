## MODIFIED Requirements

### Capability: workflow-log

执行日志持久化，记录 agent 执行过程中的所有 ACP 事件。

#### Requirement: workflow_log 表存储执行事件

Server SHALL 维护 `workflow_log` 表，记录每次 spawn_coder 执行过程中的 ACP 事件。每条记录包含 issue_id、event_type 和完整 JSON data payload。

##### Scenario: 记录 tool_call 事件
- **WHEN** opencode acp 子进程报告一个 tool_call sessionUpdate
- **THEN** 一条记录插入 workflow_log，event_type 为 'tool_call'，data 包含 tool name、input、status、locations 等完整信息

##### Scenario: 记录 agent_message_chunk 事件
- **WHEN** opencode acp 子进程报告一个 agent_message_chunk sessionUpdate
- **THEN** 一条记录插入 workflow_log，event_type 为 'agent_message_chunk'，data 包含 text content

##### Scenario: 记录执行结束信息
- **WHEN** spawn_coder 执行完成（成功或超时）
- **THEN** workflow_log 中包含最终的 stopReason 和 usage 信息

##### Scenario: 记录模型选择事件
- **WHEN** spawn_coder 成功选择模型并启动 session
- **THEN** 一条记录插入 workflow_log，event_type 为 'model_selected'，data 包含 { model, stage, source }

##### Scenario: 记录模型回退事件
- **WHEN** spawn_coder 因配置的模型不可用触发回退
- **THEN** 一条记录插入 workflow_log，event_type 为 'model_fallback'，data 包含 { configured, fallback, stage, reason }

#### Requirement: 查询执行日志

API SHALL 提供查询某个 issue 执行日志的端点。

##### Scenario: 获取 issue 的执行日志
- **WHEN** 请求 `GET /api/issues/:number/logs`
- **THEN** 返回该 issue 的所有 workflow_log 记录，按 created_at 升序排列
- **AND** 支持可选的 `?eventType=tool_call` 过滤

##### Scenario: 按 session 聚合事件
- **WHEN** 查询某个 session_id 的所有事件
- **THEN** 返回该 ACP session 的所有 workflow_log 记录，按 created_at 升序排列
- **AND** 可用于重放某次 spawn_coder 的完整执行过程

##### Scenario: 按 eventType 过滤模型事件
- **WHEN** 请求 `GET /api/issues/:number/logs?eventType=model_selected`
- **THEN** 仅返回 model_selected 类型的记录
- **WHEN** 请求 `GET /api/issues/:number/logs?eventType=model_fallback`
- **THEN** 仅返回 model_fallback 类型的记录
