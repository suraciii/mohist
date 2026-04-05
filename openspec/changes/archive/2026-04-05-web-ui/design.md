## Context

mohist 使用 Hono 作为 HTTP 框架，SQLite 作为存储，已有完整的 REST API（projects、issues、labels、config、health/status）。前端技术栈：React + Vite + Tailwind CSS + TanStack Query。

**关键架构事实：**

- Agent Runner（`runMainAgent`）运行在 Server 同一进程内，使用 Vercel AI SDK `streamText()` + tool calling
- MainAgent 拥有 5 个 tools：`read_workflow`、`spawn_coder`、`advance_stage`、`add_comment`、`get_issue`
- `spawn_coder` 会 `spawn('opencode', ['acp'])` 启动子进程（这是唯一的子进程）
- Agent 状态追踪通过 `issues.ts` 中的两个模块级变量：`activeAgentIssueId`、`activeAgentPromise`
- Session（`SessionManager`）是纯内存的 `Map<string, Session>`，重启即丢失
- 当前审批机制是 prompt-based：系统提示告诉 LLM "如果 approval: true 就停下来"，没有 API、没有状态
- Workflow 阶段：`draft → plan → build → check → done`（仅 `build` 有 `approval: true`）
- `IssueStatus` 枚举：`active`、`paused`、`blocked`（独立于 Stage）
- Labels 存储为 JSON 数组在 issues 表中
- 所有 API 响应使用 `ApiResponse<T>` 信封格式

## Goals / Non-Goals

**Goals:**
- `mo server` 一条命令同时提供 API + Web UI
- 看板视图实时展示 Issue 工作流状态
- SSE 实时推送 stage 变化、评论添加、Agent 状态
- 用户可通过 Web UI 审批 gate、管理 Issue

**Non-Goals:**
- 不做认证/授权（本地工具，localhost 使用）
- 不做 token 级实时推送（Level 2 动作级事件后续迭代）
- 不做移动端适配
- 不做 TUI（保持纯 CLI）
- 不做多用户/多会话
- 不做多 Agent 并行（MVP 保持单 Agent 限制）

## Decisions

### 1. 嵌入式 UI（参考 opencode）

**决策**: Vite 构建产物嵌入 server，同源服务。

**理由**: 零部署复杂度、无 CORS 问题、`mo server` 一条命令搞定。开发时 Vite dev server proxy 到后端，生产时 `serveStatic` 提供构建产物。

**替代方案**: 独立前端仓库/进程 → 增加部署复杂度和 CORS 配置。

### 2. SSE 实时推送（Hono streamSSE）

**决策**: 使用 Hono 内置 `streamSSE` 提供 `/api/events` endpoint。

**理由**: mohist 场景主要是"看进度"，单向推送足够。比 WebSocket 简单，Hono 原生支持。

**事件类型 (Level 1)**:
- `stage_changed` — issue stage 变化
- `comment_added` — 新评论
- `agent_started` / `agent_completed` / `agent_error` — Agent 生命周期
- `approval_requested` — 到达审批 gate

**事件来源**: Agent tools（`advance_stage`、`add_comment`）直接通过注入的 EventBus emit 事件。无需文件 IPC 或跨进程通信。

**SSE 项目隔离**: SSE 连接通过 `GET /api/events?projectId=xxx` 参数限定项目。Server 端过滤事件，仅推送与该项目相关的 event。客户端在切换项目时重建 SSE 连接。

### 3. In-Process EventBus

**决策**: 创建 in-process EventBus 单例，注入到 Agent tools 和 API routes 中，作为 SSE 事件的数据源。

**理由**: MainAgent 运行在 Server 同进程内。`advance_stage` tool 执行 `issueRepo.updateStage()` 后可直接 `eventBus.emit('stage_changed', ...)`。`add_comment` tool 同理。无需任何跨进程通信机制。

**替代方案 — Signal File IPC（已否决）**:
nanoclaw 使用文件 IPC 是因为 Agent 运行在 Docker 容器中（文件系统隔离），而 mohist 的 MainAgent 运行在 Server 同进程内。文件 IPC 在这里是不必要的复杂度。

```
正确的数据流:

┌──────────────────────────────────────────────────┐
│                 Server Process                    │
│                                                   │
│  Agent Tools (in-process)                         │
│    ├── advance_stage → issueRepo.updateStage()    │
│    │                    eventBus.emit('stage_changed')│
│    │                                              │
│    ├── add_comment  → commentRepo.create()        │
│    │                    eventBus.emit('comment_added')│
│    │                                              │
│    └── spawn_coder  → child_process.spawn(...)    │
│                         ↑ 唯一的子进程             │
│                         │ 不需要 signal files      │
│                         │ spawn_coder 的结果通过   │
│                         │ Promise 返回给 agent loop│
│                                                   │
│  EventBus ──── subscribe ────→ SSE /api/events    │
│                                                   │
└──────────────────────────────────────────────────┘
```

### 4. Approval Gate: Stop & Resume 模式

**决策**: Agent 在 gate 处停止当前 session，将 stage output 持久化到 DB。用户审批后启动新 agent session，从持久化的上下文恢复。

**理由**: MainAgent 的 `streamText()` 循环自然结束即停止。Session 是纯内存的，无需"exit"任何进程。关键是需要解决**上下文持久化**问题——新 session 需要前一个 stage 的 output。

**流程**:
```
1. Agent 完成 plan stage，检查 workflow: build.approval = true
2. advance_stage tool:
   a. issueRepo.updateStage(issueId, 'build')      ← 先推进 stage
   b. eventBus.emit('approval_requested', {issueId, stage: 'build'})
   c. 返回 "Approval required for build stage. Agent stopping."
3. Agent loop 收到 tool result，add_comment 总结 plan output
4. Agent loop 自然结束（LLM 不再调用 tool）
5. runMainAgent() promise resolves
6. activeAgentPromise = null
7. Issue 状态: stage=build, status=active

--- 用户在 Web UI 点击 "Approve & Continue" ---

8. POST /api/issues/:number/approve
9. 从 comments 中提取最新 agent comment 作为 stage context
10. 启动新 agent session:
    - system prompt 包含 issue 信息 + 当前 stage
    - 注入 plan output 作为 {plan.output} 变量
    - agent 开始执行 build stage 的 prompt
11. eventBus.emit('agent_started', {issueId})
```

**上下文传递机制**:
- 每个 stage 完成时，agent 通过 `add_comment` 记录 output（已有行为）
- 恢复时，从 DB comments 中按时间倒序找到最后一个 agent comment
- 将 comment body 作为 `{plan.output}` 等变量注入新 session 的 system prompt
- 无需修改 SessionManager（保持纯内存），利用已有的 comment 持久化

**替代方案 — Exit-Respawn（已否决）**:
原提案假设 Agent 是独立子进程，需要 exit 再 respawn。实际 MainAgent 运行在 Server 进程内，"exit" 无意义。正确模型是 session 自然结束 + 新 session 启动。

### 5. Agent Runner Service 提取

**决策**: 将 agent 运行逻辑从 `issues.ts` 的模块级变量提取到独立的 `AgentRunnerService`。

**理由**: 当前 agent 状态追踪散落在 `issues.ts` 的两个 `let` 变量中。Web UI 需要：
- 查询 agent 状态（`GET /api/agent/status`）
- Agent 生命周期事件（`agent_started`/`agent_completed`/`agent_error`）
- 未来多 agent 支持

提取后：
```typescript
class AgentRunnerService {
  private activeIssueId: string | null = null;
  private activePromise: Promise<void> | null = null;

  start(issue, context): Promise<void> { ... }
  getStatus(): AgentStatus { ... }
  isRunning(): boolean { ... }
}
```

`AgentRunnerService` 订阅 EventBus，在 agent 生命周期关键点 emit 事件。

### 6. 前端两页路由

**决策**: `/`（看板）和 `/issue/:number`（详情），使用 React Router。

**理由**: UI 复杂度低，两个页面足够覆盖所有场景。

### 7. 前端组件库: shadcn/ui + Tailwind CSS

**决策**: 使用 shadcn/ui（headless 组件）+ Tailwind CSS。

**理由**: shadcn/ui 是 React 生态最成熟的 headless 方案，Tailwind 与 opencode 保持一致。

## Risks / Trade-offs

- **SSE 重连** → 客户端断线重连后，需重新拉取全量数据（TanStack Query 自动处理），不实现事件追补
- **构建产物体积** → 嵌入式 UI 会增加 npm 包体积，但 React SPA 通常 < 500KB gzipped，可接受
- **前端开发体验** → 需要 `concurrently` 同时启动 Vite dev server 和 Hono 后端，通过 proxy 转发 API 请求
- **单 Agent 限制** → MVP 保持当前单 agent 限制，Web UI 在 agent 运行时禁用其他 issue 的 start 按钮
- **Agent 恢复上下文** → 依赖 comment 持久化传递 stage output。如果 agent 未能正确记录 output，后续 stage 可能缺乏上下文。通过系统 prompt 强制要求 agent 在每个 stage 结束时 add_comment 来缓解
- **advance_stage tool 持有过期 issue 状态** → `context.issue` 在 tool 创建时设置，多步 tool call 后可能过期。需在 tool execute 中重新从 DB 读取最新 issue 状态

## Open Questions

- 看板是否支持拖拽改变 stage？（手动 stage 变更 vs 仅 agent 驱动）
- Agent 日志实时展示是否纳入 MVP？
- `{plan.output}` 变量替换机制是硬编码在 prompt template 中还是需要通用化？（当前 `spawn_coder` 的 `replaceTemplateVariables` 已支持）
