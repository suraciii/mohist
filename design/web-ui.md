# Web UI 架构设计

## 概述

Web UI 是 mohist 的嵌入式管理界面，通过浏览器提供 Issue 工作流可视化、实时状态监控和审批交互。

**技术栈**
- 后端: Hono (HTTP framework) + SQLite
- 前端: React + Vite + Tailwind CSS + TanStack Query
- 实时通信: SSE (Server-Sent Events)
- 部署: 嵌入式，与 API 同源服务

## 架构决策

### 1. 嵌入式 UI 模式

**实现**: `packages/cli/web/` 目录包含完整 React SPA，Vite 构建后产物由 Hono 静态文件中间件提供。

**关键代码** (`src/server/http-server.ts:47-65`):
```typescript
public serveStaticFiles(webDistDir: string): void {
  this.app.use('/assets/*', serveStatic({ root: resolvedDir }));
  this.app.get('*', async (c) => {
    const indexPath = path.join(resolvedDir, 'index.html');
    return c.html(fs.readFileSync(indexPath, 'utf-8'));
  });
}
```

**理由**: 零部署复杂度、`mo server` 单条命令启动、无 CORS 问题。

### 2. In-Process EventBus

**实现**: 基于 Map 的发布-订阅模式，支持 6 种事件类型。

**关键代码** (`src/services/event-bus.ts:1-63`):
```typescript
export type EventMap = {
  stage_changed: { issueId: string; projectId: string; from: string; to: string };
  comment_added: { issueId: string; projectId: string; commentId: string; body: string; createdAt: string };
  agent_started: { issueId: string; projectId: string };
  agent_completed: { issueId: string; projectId: string };
  agent_error: { issueId: string; projectId: string; error: string };
  approval_requested: { issueId: string; projectId: string; stage: string };
};

export class EventBus {
  private listeners = new Map<string, Set<ListenerEntry>>();
  on<T extends EventName>(event: T, listener: EventListener<T>): void { ... }
  emit<T extends EventName>(event: T, data: EventMap[T]): void { ... }
}
```

**数据流**:
```
Agent Tools (in-process)
  ├── advance_stage → issueRepo.updateStage() → eventBus.emit('stage_changed')
  └── add_comment  → commentRepo.create() → eventBus.emit('comment_added')
         ↓
    EventBus
         ↓
    SSE /api/events (project-scoped filtering)
```

**理由**: MainAgent 与 Server 同进程运行，无需 IPC。Tools 直接 emit 事件，SSE endpoint 订阅转发。

### 3. SSE 实时推送

**实现**: Hono `streamSSE` 提供 `/api/events?projectId=xxx`，支持项目级过滤和 30 分钟超时。

**关键代码** (`src/api/events.ts:15-66`):
```typescript
app.get('/', async (c) => {
  const projectId = c.req.query('projectId');
  return streamSSE(c, async (stream) => {
    const handler = (data: any) => {
      if (projectId && data.projectId !== projectId) return;
      stream.writeSSE({ event: eventName, data: JSON.stringify(data) });
    };
    // Subscribe to all event types
    // 30-minute timeout to prevent resource leaks
    await stream.sleep(30 * 60 * 1000);
  });
});
```

**事件类型**: `stage_changed`, `comment_added`, `agent_started`, `agent_completed`, `agent_error`, `approval_requested`

### 4. AgentRunnerService 提取

**实现**: 将原 `issues.ts` 中的模块级变量 (`activeAgentIssueId`, `activeAgentPromise`) 提取为独立服务类。

**关键代码** (`src/services/agent-runner-service.ts:14-92`):
```typescript
export class AgentRunnerService {
  private activeIssueId: string | null = null;
  private activePromise: Promise<void> | null = null;

  constructor(private readonly eventBus: EventBus) {}

  start(issue: Issue, ...): void {
    if (this.activePromise) throw new Error('Agent already running');
    this.eventBus.emit('agent_started', { issueId: issue.id, projectId });
    this.activePromise = runMainAgent({ ...eventBus: this.eventBus })
      .then(() => this.eventBus.emit('agent_completed', ...))
      .catch((err) => this.eventBus.emit('agent_error', ...));
  }

  getStatus(): AgentStatus { ... }
  isRunning(): boolean { ... }
}
```

**集成点**:
- Server 启动时创建单例 (`src/server/index.ts:63`)
- 注入到 Issue routes (`src/api/issues.ts:26`)
- 提供 `GET /api/agent/status` endpoint

### 5. Approval Check: Stop & Resume 模式

**实现**: Agent 到达 user-approval check 时 session 自然结束，用户 Approve 后启动新 session。

**关键流程**:

1. **Agent 停止** (`src/tools/advance-stage.ts:56-70`):
```typescript
if (targetStageConfig?.approval) {
  context.eventBus.emit('approval_requested', { issueId, projectId, stage });
  // Agent loop 自然结束，promise resolves
}
```

2. **用户审批** (`src/api/issues.ts:490-580`):
```typescript
app.post('/:number/approve', async (c) => {
  // 验证 issue 在 approval gate
  // 从 comments 提取最新 agent comment 作为 context
  // agentRunner.start() 启动新 session
});
```

**上下文传递**: Stage output 通过 `add_comment` 持久化到 DB，新 session 从 comments 恢复。

### 6. 工具集成

**advance_stage tool** (`src/tools/advance-stage.ts`):
- 接收可选 `eventBus` 参数
- Stage 变更后 emit `stage_changed`
- 检测到 approval gate 时 emit `approval_requested`

**add_comment tool** (`src/tools/add-comment.ts`):
- 接收可选 `eventBus` 参数
- Comment 创建后 emit `comment_added`

**MainAgent 上下文** (`src/agents/main-agent.ts:70-75`):
```typescript
toolRegistry.register(createAdvanceStageTool({ 
  issue: context.issue, 
  issueRepo: context.issueRepo,
  eventBus: context.eventBus  // 注入 EventBus
}));
```

## API 扩展

### 新增端点

| 端点 | 描述 | 关键实现 |
|------|------|----------|
| `GET /api/events?projectId=xxx` | SSE 实时事件流 | `src/api/events.ts:18-62` |
| `POST /api/issues/:number/approve` | 审批 gate 并启动新 agent session | `src/api/issues.ts:490-580` |
| `GET /api/issues/:number/diff` | 获取 git diff 统计 | `src/api/issues.ts:582-679` |
| `GET /api/agent/status` | 查询当前 Agent 状态 | `src/api/agent.ts:5-15` |

### 修改端点

- `GET /api/issues` — 支持 `?projectId=xxx` 查询参数 (`src/api/issues.ts:31`)
- `POST /api/issues/:number/start` — 使用 AgentRunnerService 替代模块级变量

## 前端架构

### 目录结构

```
packages/cli/web/
├── src/
│   ├── components/           # React components
│   │   ├── KanbanBoard.tsx    # 看板主视图
│   │   ├── IssueCard.tsx      # Issue 卡片
│   │   ├── IssueDetailPage.tsx # 详情页
│   │   └── ...
│   ├── hooks/
│   │   ├── useSSE.ts         # SSE 连接管理
│   │   └── useQueries.ts     # TanStack Query hooks
│   ├── context/
│   │   └── ProjectContext.tsx # 当前项目状态
│   └── lib/
│       ├── api.ts            # API 客户端
│       └── types.ts          # TypeScript 类型
```

### 实时更新机制

**SSE Hook** (`web/src/hooks/useSSE.ts`):
- 连接 `/api/events?projectId=xxx`
- 自动重连（指数退避）
- 项目切换时重建连接

**Query 缓存失效**:
- `stage_changed` → 刷新 issues 列表
- `comment_added` → 刷新 comments
- `agent_started/completed/error` → 刷新 agent 状态

## 部署与构建

### 开发模式

```bash
cd packages/cli && npm run dev:web    # Vite dev server (port 5173)
# 或同时启动
concurrently "npm run server" "npm run dev:web"
```

### 生产构建

```bash
cd packages/cli && npm run build:web  # 构建到 web/dist/
npm run build                         # TypeScript 编译
npm run server                        # Hono 服务静态文件
```

### 静态文件服务

构建产物 `web/dist/` 包含:
- `index.html` — SPA 入口
- `assets/` — JS/CSS 资源

Hono 配置 (`src/server/http-server.ts`):
- `/assets/*` → 静态文件
- `/api/*` → API 路由（优先）
- `*` → SPA fallback (index.html)

## 关键实现细节

### 1. EventBus 错误处理

Listener 错误被 swallow 避免影响其他 listeners (`src/services/event-bus.ts:48-50`):
```typescript
try {
  entry.listener(data);
} catch {
  // swallow listener errors
}
```

### 2. SSE 超时与清理

- 30 分钟连接超时防止资源泄漏
- Abort signal 监听客户端断开
- 正常完成时清理所有 listeners

### 3. Diff 解析

Git diff `--stat` 输出解析 (`src/api/issues.ts:655-664`):
```typescript
const match = line.match(/^( .+?)\s*\|\s*(\d+)\s*([+-]+)$/);
const additions = diffSymbols.split('+').length - 1;
const deletions = diffSymbols.split('-').length - 1;
```

### 4. 单 Agent 限制

AgentRunnerService 通过 `activePromise` 非空检查强制单 Agent (`src/services/agent-runner-service.ts:49-51`):
```typescript
if (this.activePromise) {
  throw new Error(`Agent already running on issue #${this.activeIssueNumber}`);
}
```

## 文件清单

**后端实现**:
- `src/services/event-bus.ts` — EventBus 类
- `src/services/agent-runner-service.ts` — Agent 生命周期管理
- `src/api/events.ts` — SSE endpoint
- `src/api/agent.ts` — Agent 状态 API
- `src/api/issues.ts` — Issue 操作 API (approve, diff)
- `src/server/http-server.ts` — 静态文件服务
- `src/server/index.ts` — Server 启动集成
- `src/tools/advance-stage.ts` — Stage 推进 tool (EventBus emit)
- `src/tools/add-comment.ts` — Comment tool (EventBus emit)
- `src/agents/main-agent.ts` — 注入 EventBus 到 tools

**前端实现**:
- `web/src/App.tsx` — 路由和布局
- `web/src/components/KanbanBoard.tsx` — 看板视图
- `web/src/components/IssueDetailPage.tsx` — 详情页
- `web/src/hooks/useSSE.ts` — SSE 客户端
- `web/src/hooks/useQueries.ts` — 数据获取
- `web/src/context/ProjectContext.tsx` — 项目状态

## 参考

- OpenSpec 变更提案: `openspec/changes/archive/2026-04-05-web-ui/`
