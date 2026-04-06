## Context

Server 已有 SSE 端点 `GET /api/events?projectId=xxx`，使用 Hono 的 `streamSSE` 实现，支持 7 种事件类型：
- agent_started - agent 开始执行
- agent_completed - agent 正常完成  
- agent_paused - agent 暂停等待用户输入
- agent_error - agent 执行出错
- stage_changed - 阶段变更
- comment_added - 添加评论
- approval_requested - 请求审批

CLI 使用 Commander.js，已有 `requireServer()` 守卫和 `apiClient()` HTTP 客户端。

当前 CLI 只有 `chalk` 用于终端着色，没有 TUI 框架。SSE 客户端需要手动实现（Node.js 内置 `http` 模块即可）。

**⚠️ 后端修复**: `ALL_EVENT_TYPES` 数组需要添加 `'agent_paused'` 才能使 pause 事件正常发送到客户端。这是必要的后端修复。

## Goals / Non-Goals

**Goals:**

- 实现 read-only 的 `mo attach` 命令
- 实时显示 agent 生命周期事件（started、paused、completed、error）
- 实时显示 stage 变更事件
- 实时显示 comment 和 approval 事件
- 支持 `--project` 过滤（按项目名称解析为 ID）
- 支持 `--follow` 自动重连（简单重连，不带 Last-Event-ID）
- 优雅退出（SIGINT 和 SIGTERM）

**Non-Goals:**

- 不实现交互式消息注入（stdin → API）——这是 message-injection change
- 不实现 ask_user 问题回复——这是 ask-user change
- 不做 Last-Event-ID 断点续传（保持只读，接受可能的重复事件）
- 不做复杂的 TUI（滚动、搜索、过滤等）
- 不引入新依赖（ink、blessed 等）

## Decisions

### D1: 使用 Node.js 内置 http 模块实现 SSE 客户端

SSE 协议非常简单（`event: xxx\ndata: xxx\n\n`），不需要第三方库。

使用 `http.get()` 连接 SSE 端点，逐行解析 `event:` 和 `data:` 前缀，触发回调。

**替代方案**：使用 `eventsource` npm 包。不采用——避免增加依赖，手动解析代码量也很小。

### D2: 事件格式化输出

使用 `chalk` 着色，格式如下：

```
[12:34:56] >> agent started          issue #3 "Add search feature"
[12:34:57] -> stage changed          issue #3: plan -> build
[12:35:01] ## comment added          issue #3: "Plan complete, starting build..."
[12:38:42] || agent paused           issue #3 (waiting for input)
[12:42:30] ok agent completed        issue #3
[12:42:31] !! agent error            issue #3: "API call failed"
[12:45:00] ?? approval requested     issue #3: "Review design.md"
```

颜色映射：
- agent_started: green
- agent_completed: green
- agent_paused: yellow
- approval_requested: yellow
- stage_changed: cyan
- comment_added: white
- agent_error: red

### D3: --project 过滤

`mo attach --project myapp` 的工作流程：
1. 调用 `/api/projects` 获取所有项目
2. 查找 name 匹配的项目，获取其 id
3. 构建 SSE URL: `/api/events?projectId=<id>`

如果不带 `--project` 参数：
- 尝试读取当前目录的 mohist 配置获取当前项目 ID
- 如果不在项目目录中，连接到 `/api/events`（无过滤，显示所有事件）

### D4: --follow 自动重连（简单重连）

`mo attach --follow` 的工作流程：
1. 连接到 SSE 端点
2. 如果连接断开，打印 "Reconnecting..."
3. 等待 2 秒后重新连接
4. 继续接收新事件

**注意**: 不使用 Last-Event-ID 断点续传。保持只读原则，接受可能收到重复事件。

重连次数无限制，每次失败都等待 2 秒后重试。

### D5: 优雅退出

监听 SIGINT（Ctrl+C）和 SIGTERM，关闭 HTTP 连接，打印 "Detached." 后退出。

实现方式：在信号处理器中调用 `stream.destroy()` 关闭连接，让程序自然退出。

不使用 `process.exit()` 强制退出，让自然退出流程完成。

### D6: Server 未运行检测

复用现有的 `requireServer()` 函数（在 `cli/index.ts` 中定义），它会检查 `/api/health` 端点。

如果服务器未运行，显示：
```
Error: Server is not running
Start the server with: mo server start
```
然后以状态码 1 退出。

### D7: 后端修复（必要）

`/api/events.ts` 中的 `ALL_EVENT_TYPES` 数组需要添加 `'agent_paused'`：

```typescript
const ALL_EVENT_TYPES: EventName[] = [
  'stage_changed',
  'comment_added',
  'agent_started',
  'agent_completed',
  'agent_error',
  'approval_requested',
  'agent_paused',  // <-- 添加这一行
];
```

这是必要的后端修复，否则 agent_paused 事件不会发送到客户端。

## Risks / Trade-offs

- **[Low] 无新依赖** → 手动 SSE 解析可能有 edge case（如 data 包含多行 JSON），但 Hono 的 streamSSE 输出格式可控，实际不会遇到
- **[Low] 只读** → 不修改业务逻辑代码，只添加事件订阅，回归风险极低
- **[Low] 无断点续传** → 使用 `--follow` 重连时可能收到重复事件，但在监控场景下可接受
- **[Accepted] 后端小修复** → 需要添加 'agent_paused' 到订阅列表，但这是让功能正常工作的必要修复
- **[Future] 交互版本** → 当前只做 read-only，后续 message-injection change 完成后可以扩展 stdin 输入
