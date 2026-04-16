## Context

mohist 通过 `runAgentLoop` (Vercel AI SDK `streamText`) 运行 Main Agent orchestrator，再通过 `spawn('opencode', ['acp'])` 启动 Coder Agent 子进程。当前状态：

- **Main Agent session**：`SessionManager` 使用内存 `Map<string, Session>` 存储，进程重启即丢失。`agent-loop.ts` 的 `fullStream` 产生 `text-delta`、`tool-call`、`tool-result` 事件，通过 EventBus 实时广播但不持久化。
- **Coder Agent (ACP)**：`acp-session.ts` 已将每个 ACP notification 写入 `workflow_log` 表（SQLite 持久化）。`acpSessionId` 在 ACP session 完成后返回但未记录。
- **WebUI**：`useSSE.ts` 只订阅了 8 个高层事件（`agent_started` 等），仅用于触发 query invalidation。`IssueDetailPage.tsx` 对运行中的 agent 只显示 "Agent is running..." 静态蓝框。

## Goals / Non-Goals

**Goals:**
- Main Agent 的完整对话（思考文字、tool call args/result）持久化到 SQLite，支持事后查询
- Coder Agent 的 session 映射（issue_id ↔ acpSessionId）持久化，详情从已有 workflow_log 查询
- WebUI 实时展示 agent 的 streaming text、tool call timeline、任务进度
- WebUI 支持事后回看 agent 完整会话历史

**Non-Goals:**
- 不直接读取 opencode 的 SQLite 数据库（避免耦合）
- 不修改 opencode 或 ACP 协议
- 不实现 agent 对话的编辑/删除/重放功能
- 不实现 Main Agent session 的 pause/resume 持久化（当前内存 session 恢复机制不变）

## Decisions

### Decision 1: Main Agent message 持久化粒度 — 按 run 结束后批量写入

**选择**: `runAgentLoop` 的 `fullStream` 完全结束后，按 `result.steps` 批量将 messages 写入 `agent_session_message` 表。不逐 chunk 写入，也不在每个 step 中间写入。

**理由**: `agent_text_chunk` 事件频率极高（逐字），逐 chunk 写 DB 会严重影响 LLM 推理性能。`result.steps` 在 `fullStream` 结束后才能完整拿到，此时一次性写入所有 steps 的 messages 是最简单且安全的做法。

**影响**: 由于写入发生在 `runAgentLoop` 完成后，如果用户在**当前 run 尚未结束**时打开 IssueDetailPage，历史 API 不会返回本次 run 的数据，页面只能依赖 SSE 接收当前 run 的实时事件。这是可接受的 trade-off。

**备选**: 
- 逐 message 写入：频率仍较高，且 Vercel AI SDK 的 message 格式在 step 间可能有重叠
- 在 `fullStream` 内按 step 写入：需要依赖 Vercel AI SDK 的 `step-finish` 事件，实现复杂度较高，收益有限

### Decision 2: Coder session 详情来源 — 使用已有 workflow_log

**选择**: Coder Agent 的详细内容从 `workflow_log` 表查询，通过 `acpSessionId` 过滤。不引入新的存储依赖。

**理由**: `acp-session.ts:166-172` 已经将每个 ACP notification（含 `agent_message_chunk`、`tool_call`、`tool_call_update` 等）写入 `workflow_log`。数据已存在，无需重复存储。

**备选**:
- 直接读 opencode SQLite：耦合太强，且 opencode DB 路径不确定
- 通过 opencode HTTP API 查询：需要 opencode server 在运行，增加依赖

### Decision 3: WebUI 实时 + 历史合并策略

**选择**: 进入 IssueDetailPage 时，先拉取历史数据（`GET /agent-session` + `GET /coder-sessions`），然后 append 实时 SSE 事件。使用 `(stepIndex, executionId)` 组合去重——若 SSE 事件的 `stepIndex` 和 `executionId` 已存在于历史数据中，则丢弃。

**stepIndex 语义统一**: 
- DB 中的 `step_index` 严格等于 `result.steps` 数组索引（从 0 开始）。
- SSE 事件中的 `stepIndex` 也需要对齐到该索引，避免历史数据与实时事件错位。

**理由**: 历史数据是完整的 messages，SSE 事件是增量的 chunks/timeline。由于 runAgentLoop 的持久化在完成后才发生，mid-run 打开页面时历史 API 可能为空，此时页面将完整依赖 SSE。去重逻辑主要针对"agent 已完成，用户刷新页面"的场景。

### Decision 4: streaming text 渲染性能 — useRef + 批量更新

**选择**: 前端用 `useRef` 存储 text buffer，SSE 的 `agent_text_chunk` 事件追加到 ref buffer，用 `requestAnimationFrame` 或 100ms interval 批量触发 setState 更新渲染。

**理由**: `agent_text_chunk` 逐字触发，每个都 setState 会导致严重卡顿。

### Decision 6: Coder session 映射写入时机

**选择**: 在 `acp-session.ts` 中，当 `sessionResult.sessionId` 成功返回后（line 247 附近）立即插入 `coder_session` 行，状态为 `running`。若后续 `prompt` 等待或执行失败，则在 `finally/catch` 中更新状态为 `failed`。

**理由**: `sessionId` 生成之前（initialize/newSession）的 timeout 属于 ACP 连接失败，不应记录为一次有效的 coder session。只有在 session 创建成功后，才算一次"已开始"的 session。

### Decision 7: SSE 状态管理方案

**选择**: `useSSE` 扩展为支持一个轻量级全局事件派发器（如 `EventTarget`）。`AgentSessionPanel` 在 mount 时注册 listener，unmount 时移除。保持单一 SSE 连接，由各组件按需消费子集事件。

**理由**: 避免 `AgentSessionPanel` 单独创建 EventSource 造成重复连接，也避免把状态硬耦合进 `useSSE` 导致页面间污染。

### Decision 5: 数据库表设计

**`agent_session_message` 表**:
```sql
CREATE TABLE agent_session_message (
  id TEXT PRIMARY KEY,
  issue_id TEXT NOT NULL,
  session_id TEXT NOT NULL,
  role TEXT NOT NULL,           -- 'user' | 'assistant' | 'system' | 'tool'
  content TEXT,                 -- text content
  tool_calls TEXT,              -- JSON array of tool calls (assistant messages)
  tool_call_id TEXT,            -- tool call ID (tool result messages)
  tool_name TEXT,               -- tool name (tool result messages)
  tool_result TEXT,             -- tool result content
  step_index INTEGER NOT NULL,  -- aligns with result.steps array index
  message_index INTEGER NOT NULL, -- index within the step's response.messages array
  created_at TEXT NOT NULL
);
```

**`coder_session` 表**:
```sql
CREATE TABLE coder_session (
  id TEXT PRIMARY KEY,
  issue_id TEXT NOT NULL,
  acp_session_id TEXT NOT NULL,
  execution_id TEXT,            -- mohist tool call execution ID
  task_description TEXT,        -- truncated task text
  status TEXT NOT NULL DEFAULT 'running',  -- 'running' | 'completed' | 'failed'
  created_at TEXT NOT NULL,
  completed_at TEXT
);
```

## Risks / Trade-offs

- **[Main Agent 中途崩溃丢失]**: Main Agent 在 step 之间崩溃时，由于持久化只在 `runAgentLoop` 完成后发生，本次 run 的全部 messages 都会丢失。→ 可接受，因为 agent runner 会在更高层重试或报错。
- **[Mid-run 打开页面无历史]**: 如果用户在当前 `runAgentLoop` 尚未结束时打开 IssueDetailPage，历史 API 不会返回本次 run 的数据，页面只能看到打开之后的 SSE 事件。→ 明确为可接受的 trade-off，在 UI 中通过 "Agent is running..." 的实时流状态自然过渡。
- **[workflow_log 数据量]**: ACP notification 写入 workflow_log 的频率较高，长时间运行的 coder session 可能产生大量记录。→ 已有截断机制，且前端分页加载。
- **[Parallel tool calls 的 executionId 串线]**: 当前 `agent-loop.ts` 使用单变量 `currentExecutionId`，parallel tool calls 时第二个调用会覆盖第一个的 ID，导致 `main_tool_call` 事件的 started/completed 无法正确配对。→ **必须修复**：改用 `toolCallId → executionId` 的 Map 来追踪。
- **[SSE 事件丢失]**: 用户在 agent 运行中途打开页面，之前的 SSE 事件已丢失。→ 本次 run 的历史数据在 run 完成前不可用，合并策略仅对"已完成 run + 新实时事件"生效。
