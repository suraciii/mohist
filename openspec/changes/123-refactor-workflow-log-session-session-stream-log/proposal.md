## Why

`workflow_log` 表混合存储了两类语义完全不同的数据——workflow 级别事件（build/check/task 状态推进）和 session 实时流数据（agent thought/message chunk、tool call 细节）。实测显示 session 流数据占比高达 97%，导致 issue timeline 查询拉取大量无用 chunk、索引过滤效率极低、前端加载数据中 `workflowLogs` 字段被加载却未使用。需要将 session 流数据分离到独立的 `session_stream_log` 表，使两张表各归其位、各按其查询模式优化。

## What Changes

- 新建 `session_stream_log` 表，存储 `agent_thought_chunk`、`agent_message_chunk`、`tool_call`、`tool_call_update`、`user_message_chunk` 等 session 粒度事件
- 修改写入端：`acp-session.ts` 的 `sessionUpdate` handler 将 session 流事件写入 `session_stream_log` 而非 `workflow_log`
- 修改读取端：`/api/issues/:number/coder-sessions` 从 `session_stream_log` 查询 session 的 logs 数据
- 移除 `useIssueTimeline.ts` 中未使用的 `api.getWorkflowLogs()` 调用
- `workflow_log` 表仅保留 workflow 级别事件（build/check/task/plan/usage_update/acp_session 等事件类型）
- DB schema version 升级（migration）
- **BREAKING**：`GET /api/issues/:number/logs` 返回结果将不再包含 session chunk 数据；需要 session 详情的消费者应改用新 API 或从 `session_stream_log` 查询

## Capabilities

### New Capabilities

- **session-stream-log**：独立的 session 实时流数据存储表及 repo，按 session_id 查询 agent 交互细节（thought/message chunk、tool call）

### Modified Capabilities

- **workflow-log**：scope 收缩为仅存储 workflow 级别事件，移除 session chunk 写入职责；`GET /api/issues/:number/logs` 仅返回 workflow 事件
- **session-timeline-ui**：历史数据源从 `workflow_log` 切换到 `session_stream_log`（重建 rounds 的数据来源变更）

## Impact

- **DB layer**：新增 `session_stream_log` 表、索引；schema migration；`StateManager` 注册新 repo
- **acp-session.ts**：`sessionUpdate` handler 写入目标表变更
- **API routes**：`/api/issues/:number/coder-sessions` 查询逻辑变更
- **Frontend**：`useIssueTimeline.ts` 移除无用调用；`SessionTimeline` 组件数据源可能需适配
- **No migration of historical data**（推荐）：旧数据保留在 `workflow_log`，仅新数据分流。如需查看旧 session 完整 chunk，可后续补迁移脚本
