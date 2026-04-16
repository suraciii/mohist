## Why

用户 `issue start` 后，WebUI 只显示 "Agent is running..." 蓝框，无法观察 agent 的思考过程、工具调用和任务进度。agent session 的对话历史也因纯内存存储而在 server 重启后丢失。用户需要实时看到 agent 在做什么（streaming text + tool calls），也需要事后回看 agent 的完整会话历史。

## What Changes

- **持久化 Main Agent session messages**：将当前纯内存的 `SessionManager` (Map) 中的 messages 写入 SQLite 新表 `agent_session_message`，使 orchestrator 的完整对话（思考文字、tool call、tool result）可事后查询。
- **记录 Coder session 映射**：新增 `coder_session` 表，记录 `issue_id ↔ acpSessionId` 的对应关系及状态。Coder session 的详细内容从已有的 `workflow_log` 表查询（ACP notification 已在持久化），不引入新的存储依赖。
- **新增后端 API**：
  - `GET /issues/:number/agent-session`：返回 Main Agent 的历史 messages。
  - `GET /issues/:number/coder-sessions`：返回 Coder session 列表及其 workflow_log 详情。
- **WebUI 订阅实时 SSE 事件**：前端新增订阅 `agent_text_chunk`、`main_tool_call`、`coder_text_chunk`、`coder_tool_call`、`ralph_task_update` 等已有事件，并渲染为 streaming feed。
- **新增 AgentSessionPanel 组件**：替换当前的 "Agent is running..." 静态蓝框，展示 streaming text、tool call timeline、coder session 进度。支持实时和事后两种模式。

## Capabilities

### New Capabilities
- `main-agent-session-persistence`: 持久化 Main Agent (orchestrator) 的 session messages 到 SQLite，提供查询 API。
- `coder-session-tracking`: 记录 Coder Agent (spawn_coder/ACP) 的 session 映射，从已有 workflow_log 获取详情。
- `agent-session-ui`: WebUI 实时 streaming feed 和事后回看面板，展示 agent 思考、工具调用、任务进度。

### Modified Capabilities

(无已有 spec 需要修改)

## Impact

- **后端**：`agent-runtime/agent-loop.ts`（新增 message 持久化）、`agent-runtime/session.ts`（SessionManager 扩展）、`tools/spawn-coder.ts`（记录 coder_session 映射）、`db/`（新增两张表 + repo）、`api/`（新增两个 API 端点）
- **前端**：`hooks/useSSE.ts`（订阅新事件类型）、`lib/types.ts`（新增事件类型定义）、`components/AgentSessionPanel.tsx`（新组件）、`components/IssueDetailPage.tsx`（替换蓝框）、`hooks/useAgentSession.ts`（新 hook）
- **数据库**：新增 `agent_session_message` 和 `coder_session` 两张表，`workflow_log` 表保持不变
