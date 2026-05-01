## MODIFIED Requirements

### Requirement: workflow_log 表存储执行事件
Server SHALL 维护 `workflow_log` 表，记录 workflow 级别事件（build/check/task/plan/usage_update/acp_session 生命周期等）。每条记录包含 issue_id、event_type 和完整 JSON data payload。

Session 实时流事件（`agent_thought_chunk`、`agent_message_chunk`、`tool_call`、`tool_call_update`、`user_message_chunk`）SHALL NOT 写入 `workflow_log`，而是写入独立的 `session_stream_log` 表。

#### Scenario: 记录 acp_session_start 生命周期事件
- **WHEN** ACP session 启动
- **THEN** 一条记录插入 workflow_log，event_type 为 `'acp_session_start'`，data 包含 session 元数据

#### Scenario: 记录 task 状态事件
- **WHEN** RalphExecutor 处理任务状态变更（task_started / task_completed / task_failed）
- **THEN** 一条记录插入 workflow_log，event_type 对应任务状态，data 包含 taskId 和状态信息

#### Scenario: 记录 build/check 阶段事件
- **WHEN** build 或 check 阶段状态变更（build_started / build_completed / check_started / check_completed 等）
- **THEN** 一条记录插入 workflow_log，event_type 对应阶段事件

#### Scenario: agent_message_chunk 不再写入 workflow_log
- **WHEN** opencode acp 子进程报告一个 agent_message_chunk sessionUpdate
- **THEN** 该事件写入 `session_stream_log` 而非 `workflow_log`

#### Scenario: tool_call 不再写入 workflow_log
- **WHEN** opencode acp 子进程报告一个 tool_call sessionUpdate
- **THEN** 该事件写入 `session_stream_log` 而非 `workflow_log`

### Requirement: 查询执行日志
API SHALL 提供查询某个 issue 执行日志的端点。`GET /api/issues/:number/logs` SHALL 仅返回 `workflow_log` 中的 workflow 级别事件（不再包含 session chunk 数据）。

#### Scenario: 获取 issue 的 workflow 日志
- **WHEN** 请求 `GET /api/issues/:number/logs`
- **THEN** 返回该 issue 的所有 workflow_log 记录（不包含 session_stream_log 数据），按 created_at 升序排列
- **AND** 支持可选的 `?eventType=tool_call` 过滤

#### Scenario: 按 session 聚合事件从 session_stream_log 查询
- **WHEN** `GET /api/issues/:number/coder-sessions` 查询某个 session 的日志
- **THEN** session 的 `workflowLogs` 字段 SHALL 从 `session_stream_log` 表查询（通过 `SessionStreamLogRepo.findBySessionId`），而非 `workflow_log`
