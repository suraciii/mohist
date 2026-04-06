## Context

Server 已有 SSE 端点 `GET /api/events?projectId=xxx`，使用 Hono 的 `streamSSE` 实现，支持 6 种事件类型（ux-bug-fixes 后将增加到 7 种，含 agent_paused）。CLI 使用 Commander.js，已有 `requireServer()` 守卫和 `apiClient()` HTTP 客户端。

当前 CLI 只有 `chalk` 用于终端着色，没有 TUI 框架。SSE 客户端需要手动实现（Node.js 内置 `http` 模块即可）。

## Goals / Non-Goals

**Goals:**

- 实现 read-only 的 `mo attach` 命令
- 实时显示 agent 生命周期事件（started、paused、completed、error）
- 实时显示 stage 变更事件
- 实时显示 comment 事件
- 支持 `--project` 过滤和 `--follow` 自动重连
- 优雅退出

**Non-Goals:**

- 不实现交互式消息注入（stdin → API）——这是 message-injection change
- 不实现 ask_user 问题回复——这是 ask-user change
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
[12:34:56] ▶ agent started          issue #3 "Add search feature"
[12:34:57] → stage changed           issue #3: plan → build
[12:35:01] 💬 comment added           issue #3: "Plan complete, starting build..."
[12:38:42] ⏸ agent paused            issue #3 (approval needed at build)
[12:40:15] ▶ agent resumed           issue #3
[12:42:30] ✓ agent completed          issue #3
```

不使用 emoji（项目约定），改用 ASCII 符号：

```
[12:34:56] >> agent started          issue #3 "Add search feature"
[12:34:57] -> stage changed           issue #3: plan -> build
[12:35:01] ## comment added           issue #3: "Plan complete, starting build..."
[12:38:42] || agent paused            issue #3 (approval needed at build)
[12:40:15] >> agent resumed           issue #3
[12:42:30] ok agent completed          issue #3
```

### D3: --project 过滤

`mo attach --project myapp` 传递 `?projectId=xxx` 到 SSE 端点。不指定时使用当前活跃项目（从 config 读取）。

### D4: --follow 自动重连

`mo attach --follow` 在 SSE 连接断开后自动重连（延迟 2 秒）。不带 `--follow` 时断开即退出。

### D5: 优雅退出

监听 SIGINT（Ctrl+C）和 SIGTERM，关闭 HTTP 连接，打印 "Detached." 后退出。

不使用 `process.exit()` 强制退出，让自然退出流程完成。

## Risks / Trade-offs

- **[Low] 无新依赖** → 手动 SSE 解析可能有 edge case（如 data 包含多行 JSON），但 Hono 的 streamSSE 输出格式可控，实际不会遇到
- **[Low] 只读** → 不修改任何后端代码，回归风险为零
- **[Future] 交互版本** → 当前只做 read-only，后续 message-injection change 完成后可以扩展 stdin 输入
