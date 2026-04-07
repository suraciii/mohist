## Context

mohist 当前有两种交互模式：CLI 命令（`mo issue start` 等）和 Web UI（看板 + 详情页）。两者都是围绕 Pipeline 模式设计的——用户创建 Issue、启动 Agent、Agent 自主执行 plan→build→check。

在启动 Pipeline 之前，用户需要想清楚"做什么"和"怎么做"。这个需求探索阶段没有工具支持。用户要么自己在代码库里翻找然后手写 Issue 描述，要么在脑中想好再输入给 mohist。

**关键架构事实：**

- `runAgentLoop` 使用 Vercel AI SDK `streamText()`，支持 tool calling，内部调用 `consumeStream()` 等待完成后返回 `AgentLoopResult`
- `SessionManager` 纯内存，`Session` 包含 `ModelMessage[]`，重启即丢失
- `ToolRegistry` 已支持工具注册和组合，`Tool.define()` 是工具创建的标准方式
- `EventBus` 已有 11 种事件类型，SSE endpoint 已实现 pub-sub 模式
- 当前 Agent 工具全部面向 Pipeline 执行（advance_stage、spawn_coder、add_comment 等）
- 前端使用 React + TanStack Query + SSE，已有 `useSSE` hook 和 `api` 客户端
- 数据库使用 SQLite，已有 issues、comments、questions、workflow_log 等表

## Goals / Non-Goals

**Goals:**

- 提供 AI 驱动的对话界面，让用户和 Agent 一起探索需求、理解代码库
- 探索会话独立于 Issue，Issue 是探索的产出物（不是前提）
- 探索完成后可将对话精华结晶为结构化 Issue 描述，进入 Pipeline
- 消息历史持久化到 SQLite，支持 server 重启后恢复
- 前端支持流式输出、tool call 折叠展示、markdown 渲染

**Non-Goals:**

- 不做多 session 并行（一次一个探索会话）
- 不做 explore session 内执行代码（只读工具集）
- 不做 session fork / branch（线性对话）
- 不做 agent 主动发起探索（只有用户触发）
- 不做探索会话绑定 Issue（Issue 是产出物，不是容器）
- 不做移动端适配
- 不做认证/授权

## Decisions

### 1. Explore Session 独立于 Issue

**决策**: Explore Session 是独立实体，不绑定 Issue。Issue 是探索的产出物。

**理由**: 探索的起点是模糊的想法，不是明确的 Issue。强制先建 Issue 再探索会打断思考流。探索可能产出 Issue，也可能只是理解了代码库然后放弃——两者都应该支持。

**数据模型**:

```sql
CREATE TABLE explore_sessions (
  id TEXT PRIMARY KEY,
  project_id TEXT NOT NULL REFERENCES projects(id),
  issue_id TEXT REFERENCES issues(id),
  title TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'active',  -- active | crystallized | archived
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE TABLE explore_messages (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES explore_sessions(id) ON DELETE CASCADE,
  role TEXT NOT NULL,          -- user | assistant
  content TEXT NOT NULL,
  tool_calls TEXT,             -- JSON: [{name, args, result}] | null
  created_at TEXT NOT NULL
);
```

`issue_id` 初始为 null，探索结晶后填充。一个 session 最多关联一个 issue。

### 2. Explore Agent：独立的 System Prompt 和工具集

**决策**: 创建独立的 `explore-agent.ts`，拥有专用 system prompt 和只读工具集。不修改 Main Agent。

**理由**: Main Agent 的 prompt 和工具集面向 Pipeline 执行（advance_stage、spawn_coder），和 Explore 的需求完全不同。两者职责正交，不应该耦合。

**System Prompt 角色**: 思考伙伴——好奇而非催促，主动读代码验证假设，用 ASCII 图帮助可视化，当需求收敛时提议 crystallize。

**工具集**:

| 工具 | 描述 | 来源 |
|------|------|------|
| `read_file` | 读取文件内容，支持行号范围 | 新增，Explore Agent 首个消费者 |
| `glob` | 按模式查找文件 | 新增 |
| `grep` | 正则搜索文件内容 | 新增 |
| `create_issue` | 将探索成果结晶为 Issue | 新增，Explore Agent 专用 |

**与 Main Agent 的工具隔离**:

```
MainAgent tools:              ExploreAgent tools:
├── read_workflow             ├── read_file
├── spawn_coder               ├── glob
├── advance_stage             ├── grep
├── add_comment               └── create_issue  ← 桥接工具
├── get_issue
└── ask_user
```

`read_file`、`glob`、`grep` 作为独立工具文件实现，未来 M3 的 sub-agent 体系（Plan Agent、Check Agent）也可以复用。

### 3. create_issue 桥接工具

**决策**: Explore Agent 通过 `create_issue` 工具将探索成果转化为 Issue。

**流程**:

```
Agent 调用 create_issue({
  title: "统一错误处理到 Result 类型",
  body: "## 背景\n...\n## 期望行为\n...\n## 约束\n...",
  labels: ["refactor"]
})
→ IssueService.create() 创建 draft issue
→ ExploreSessionRepo.updateIssueId(sessionId, issue.id)
→ ExploreSessionRepo.updateStatus(sessionId, 'crystallized')
→ eventBus.emit('explore_crystallized', { sessionId, issueId })
```

Agent 的 system prompt 应指导它在调用 `create_issue` 时：
- 从对话历史中提炼结构化描述（背景、期望行为、约束、非目标）
- 保持标题简洁
- 添加合适的标签

### 4. 消息响应采用 Request-Response Streaming

**决策**: `POST /api/explore/:id/messages` 返回 SSE stream（request-response 模式），不是通过现有 EventBus pub-sub。

**理由**: 现有 EventBus SSE 是 pub-sub——订阅后持续接收所有项目事件。Explore 的消息响应是 request-response——用户发一条消息，等 agent 的流式回复。两者语义不同。

```
POST /api/explore/:id/messages
Content-Type: application/json
{ "content": "错误处理太乱了" }

Response: text/event-stream
data: {"type":"tool_call","tool":"glob","args":"**/*.ts","result":["src/a.ts",...]}
data: {"type":"tool_call","tool":"read_file","args":{"path":"src/a.ts"},"result":"..."}
data: {"type":"chunk","content":"我看到三种"}
data: {"type":"chunk","content":"错误处理模式..."}
data: {"type":"done","issueId":null}
```

事件类型：
- `tool_call`: Agent 调用了工具，包含工具名、参数和结果
- `chunk`: Agent 回复的文本片段（流式）
- `done`: 响应结束，可选包含 `issueId`（如果 agent 调用了 create_issue）

**实现方式**: 不使用现有的 `runAgentLoop`（它会 `consumeStream()` 等待全部完成）。改为直接使用 `streamText()` 并在 stream 过程中通过 SSE 实时转发 tool_call 和 text chunk。

### 5. 前端独立路由

**决策**: `/explore/:id` 独立路由页面，不做侧边栏。Header 增加 Explore 入口。

**理由**: 聊天界面需要宽度来渲染代码块和 ASCII 图，侧边栏太窄会严重影响可读性。独立路由也简化了实现。

**前端结构**:

```
/explore          → 重定向到最新 active session，或创建新 session
/explore/:id      → 聊天界面
```

**组件**:

| 组件 | 职责 |
|------|------|
| `ExplorePage` | 页面容器，加载 session 和消息历史 |
| `ExploreChat` | 消息列表，虚拟滚动 |
| `ExploreMessage` | 单条消息渲染（user/assistant），markdown 支持 |
| `ExploreToolCall` | tool call 折叠/展开（显示工具名、参数、结果） |
| `ExploreInput` | 输入框，发送消息 |

### 6. 消息历史持久化与恢复

**决策**: Explore 消息存储在 SQLite `explore_messages` 表中。恢复时从 DB 加载历史消息重建 LLM 对话上下文。

**存储格式**:

- `role`: `user` 或 `assistant`
- `content`: 纯文本（agent 的回复文本，不包含 tool call 的中间结果）
- `tool_calls`: JSON 数组（tool call 记录，用于前端展示，不发送给 LLM）

**恢复流程**:

```
1. 加载 explore_messages WHERE session_id = ?
2. 按 created_at 排序
3. 构建 messages 数组: [{role:'user',content:...}, {role:'assistant',content:...}, ...]
4. 传入 streamText({ messages, ... })
```

tool_calls 不需要传给 LLM——LLM 只需要看到最终的文本回复。Tool call 记录仅用于前端展示。

### 7. ExploreService 封装

**决策**: 创建 `ExploreService` 封装 Explore 会话的业务逻辑。

```typescript
class ExploreService {
  createSession(projectId: string, title: string): ExploreSession
  getSession(id: string): ExploreSession | null
  listSessions(projectId: string): ExploreSession[]
  deleteSession(id: string): void
  addMessage(sessionId: string, role: string, content: string, toolCalls?: ToolCallRecord[]): ExploreMessage
  getMessages(sessionId: string): ExploreMessage[]
  crystallize(sessionId: string, issueId: string): void
}
```

## Risks / Trade-offs

- **[streamText 流式转发复杂度]** → 直接使用 `streamText()` 的 async iterator，在 for-await 循环中转发每个 chunk。Vercel AI SDK 的 stream 有标准接口，复杂度可控。
- **[长对话上下文窗口溢出]** → MVP 不处理。当对话过长超过 context window 时，LLM 会报错。后续可加 compaction（截断早期消息或摘要）。backlog B-205 已记录此风险。
- **[create_issue 后对话继续]** → crystallized 状态的 session 仍然可以继续对话（比如 "再加一个约束"），Agent 可以再次调用 create_issue 创建新 issue 或建议用户编辑已有 issue。
- **[工具路径限制]** → read_file/glob/grep 的工作目录是项目路径，需要限制在项目内，防止路径遍历。使用 `path.resolve` + `startsWith` 校验。
- **[前端 streaming 连接管理]** → SSE 连接断开时，已发送的消息已持久化到 DB，重连后重新加载即可。不需要实现消息追补。

## Open Questions

- Agent 在什么时机提议 crystallize？是等用户说"差不多了"，还是 Agent 主动判断需求已收敛？倾向于两者都支持——用户可以明确说"创建 Issue"，Agent 也可以在觉得需求清晰时提议。
- `create_issue` 创建的 Issue 应该关联到哪个 worktree？explore 阶段还没有 worktree。倾向于在用户实际 start issue 时再创建 worktree（现有逻辑已支持）。
- 前端是否需要 session 列表？可以先用 Header dropdown 展示最近几个 session，不做独立列表页。
