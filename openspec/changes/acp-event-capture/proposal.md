## Why

当前 `spawn_coder` 工具与 opencode acp 子进程通信时，只捕获 `agent_message_chunk` 事件（纯文本），丢弃了所有其他 ACP SessionUpdate 事件：`tool_call`、`tool_call_update`、`plan`、`usage_update`、`stopReason` 等。输出还被硬截断为 8000 字符（head 3000 + tail 5000）。

这导致三个问题：
1. **调试困难**：agent 失败时只能看到截断的文本片段，无法知道它执行了哪些工具、修改了哪些文件、在哪一步出错
2. **审计空白**：没有 workflow_log 表，所有执行记录都是临时的，server 重启后丢失
3. **实时 UI 无数据源**：mo attach 和 Web UI 的实时进度展示需要 action-level 事件，但当前这些事件在 spawn_coder 内部就被丢弃了

这次改动是纯新增的——不修改现有行为，只增加事件捕获和持久化。为后续的 mo attach、ask_user 提供基础设施。

## What Changes

- 扩展 `spawn_coder.ts` 的 `sessionUpdate` 处理，捕获所有 ACP 事件类型
- 新增 `workflow_log` 表（SQLite），持久化执行事件
- 新增 `WorkflowLogRepo`，提供插入和查询方法
- 新增 EventBus 事件类型（`tool_call`、`tool_call_update`），用于 SSE 推送
- 输出策略改为：完整文本存入 workflow_log，返回给 Main Agent 的仍是截断摘要

## Capabilities

### New Capabilities

- `workflow-log`: 执行日志持久化，记录 agent 执行过程中的所有 ACP 事件，支持按 issue 查询

### Modified Capabilities

- `agent-runtime`: `spawn_coder` 工具捕获所有 ACP 事件并存入 workflow_log，而非只捕获 agent_message_chunk
- `event-bus`: 新增 `tool_call` 和 `tool_call_update` 事件类型，用于实时推送 agent 执行动作

## Impact

- `tools/spawn-coder.ts`: 
  - `SpawnCoderContext` 新增 `issueId: string` 和 `eventBus?: EventBus`
  - `runAcpOneshot` 签名新增 `issueId`, `workflowLogRepo`, `eventBus` 参数
  - sessionUpdate handler 新增 try/catch 错误处理
  - 捕获所有 ACP 事件类型，持久化到 workflow_log
  - 收到 tool_call 时通过 EventBus emit
- `db/migrations.ts`: 新增 migration v5 创建 workflow_log 表
- `db/workflow-log-repo.ts`: 新增 repo 文件（insert, findByIssueId, findById, findBySessionId）
- `services/event-bus.ts`: EventMap 添加 `tool_call` 事件类型
- `api/events.ts`: ALL_EVENT_TYPES 添加 `tool_call`
- `agents/main-agent.ts`: `MainAgentContext` 新增 `workflowLogRepo` 字段，传入 spawn_coder tool
- `server/index.ts`: 创建 WorkflowLogRepo 实例，注入到 MainAgentContext 和 API routes
