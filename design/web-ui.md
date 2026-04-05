# Web UI 架构设计

## 概述

Web UI 是 mohist 的嵌入式管理界面，让用户通过浏览器查看 Issue 工作流、审批 gate、管理项目。

**技术栈**: React + Vite + Tailwind CSS + TanStack Query
**实时通信**: SSE (Server-Sent Events) via In-Process EventBus
**部署模式**: 嵌入式，与 API 同源服务

## 架构决策

### 1. 嵌入式 UI 模式

**决策**: Vite 构建产物嵌入 server，同源服务。

**理由**:
- 零部署复杂度
- 无 CORS 问题
- `mo server` 一条命令同时启动 API 和 Web UI

**开发模式**: Vite dev server proxy 到后端 (`/api/*` → `http://localhost:3456`)
**生产模式**: Hono `serveStatic` 提供构建产物，SPA fallback 路由

### 2. SSE 实时推送

**决策**: 使用 Hono 内置 `streamSSE` 提供 `/api/events` endpoint。

**理由**: mohist 场景主要是"看进度"，单向推送足够。比 WebSocket 简单，Hono 原生支持。

**事件类型**:
- `stage_changed` — Issue stage 变化
- `comment_added` — 新评论添加
- `agent_started` / `agent_completed` / `agent_error` — Agent 生命周期
- `approval_requested` — 到达审批 gate

**项目隔离**: SSE 连接通过 `GET /api/events?projectId=xxx` 参数限定项目，server 端过滤只推送该项目相关事件。

### 3. In-Process EventBus

**决策**: 创建 in-process EventBus 单例，注入到 Agent tools 和 API routes。

**理由**: MainAgent 运行在 Server 同进程内，`advance_stage` 和 `add_comment` tools 可直接 `eventBus.emit()`，无需跨进程通信。

```
┌──────────────────────────────────────────────────┐
│                 Server Process                    │
│                                                   │
│  Agent Tools (in-process)                         │
│    ├── advance_stage → issueRepo.updateStage()    │
│    │                    eventBus.emit('stage_changed')│
│    ├── add_comment  → commentRepo.create()        │
│    │                    eventBus.emit('comment_added')│
│    └── spawn_coder  → child_process.spawn(...)    │
│                                                   │
│  EventBus ──── subscribe ────→ SSE /api/events    │
│                                                   │
└──────────────────────────────────────────────────┘
```

### 4. Approval Gate: Stop & Resume 模式

**决策**: Agent 在 gate 处停止当前 session，将 stage output 持久化到 DB。用户审批后启动新 agent session，从持久化的上下文恢复。

**流程**:
```
1. Agent 完成 plan stage，检查 workflow: build.approval = true
2. advance_stage tool:
   a. issueRepo.updateStage(issueId, 'build')
   b. eventBus.emit('approval_requested', {issueId, stage: 'build'})
   c. 返回 "Approval required for build stage. Agent stopping."
3. Agent loop 收到 tool result，add_comment 总结 plan output
4. Agent loop 自然结束
5. runMainAgent() promise resolves，activeAgentPromise = null

--- 用户点击 "Approve & Continue" ---

6. POST /api/issues/:number/approve
7. 从 comments 中提取最新 agent comment 作为 stage context
8. 启动新 agent session，注入 plan output 到 system prompt
9. eventBus.emit('agent_started', {issueId})
```

**上下文传递**: 每个 stage 完成时 agent 通过 `add_comment` 记录 output，恢复时从 DB 读取最后一个 agent comment 作为上下文。

### 5. Agent Runner Service

将 agent 运行逻辑从 `issues.ts` 的模块级变量提取到独立的 `AgentRunnerService`:

```typescript
class AgentRunnerService {
  private activeIssueId: string | null = null;
  private activePromise: Promise<void> | null = null;

  start(issue, context): void
  getStatus(): AgentStatus
  isRunning(): boolean
}
```

- 提供 `GET /api/agent/status` 查询
- 在 agent 生命周期关键点 emit EventBus 事件
- 支持未来多 agent 扩展

### 6. 前端路由结构

- `/` — 看板视图（Kanban Board）
- `/issue/:number` — Issue 详情页

使用 React Router 实现客户端路由，Hono 配置 SPA fallback。

### 7. 组件库选型

**shadcn/ui** + **Tailwind CSS**

- shadcn/ui: React 生态成熟的 headless 组件库
- Tailwind CSS: 与 opencode 项目保持一致

## 目录结构

```
packages/cli/
├── src/
│   ├── server/           # Hono server + static files
│   ├── api/              # API routes (issues, events, agent)
│   ├── services/         # AgentRunnerService, EventBus
│   ├── tools/            # Agent tools with EventBus injection
│   └── agents/           # MainAgent with EventBus context
└── web/                  # React SPA (Vite)
    ├── src/
    │   ├── components/   # KanbanBoard, IssueCard, IssueDetailPage
    │   ├── hooks/        # useSSE, useQueries (TanStack Query)
    │   ├── context/      # ProjectContext
    │   └── lib/          # API client, types
    └── package.json      # Frontend dependencies
```

## API 扩展

### 新增端点

- `GET /api/events?projectId=xxx` — SSE 实时事件流
- `POST /api/issues/:number/approve` — 审批 gate 并启动新 agent session
- `GET /api/issues/:number/diff` — 获取 Issue 工作目录的 git diff
- `GET /api/agent/status` — 查询当前 Agent 状态

### 修改端点

- `GET /api/issues` — 支持 `?projectId=xxx` 查询参数

## 风险与权衡

- **SSE 重连**: 客户端断线后需重新拉取全量数据（TanStack Query 自动处理），不实现事件追补
- **构建产物体积**: React SPA 通常 < 500KB gzipped，可接受
- **前端开发体验**: 需要 `concurrently` 同时启动 Vite dev server 和 Hono 后端
- **单 Agent 限制**: MVP 保持当前单 agent 限制，Web UI 在 agent 运行时禁用其他 issue 的 start 按钮
- **上下文依赖**: 依赖 comment 持久化传递 stage output，需确保 agent 正确记录

## 相关文档

- `openspec/changes/archive/2026-04-05-web-ui/` — 完整 OpenSpec 变更提案
- `packages/cli/web/` — 前端源码
