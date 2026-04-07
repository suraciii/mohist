## Context

Main Agent 使用 Vercel AI SDK 的 `streamText` 运行 LLM loop。工具的 `execute` 函数是 async 的，返回 `Promise<string>`。SDK 的 `consumeStream()` 会等待所有工具完成。

已有的 `spawn_coder` 工具已经证明了长阻塞 Promise 的可行性——它通过 ACP 子进程通信阻塞最多 30 分钟。`ask_user` 的阻塞模式与此相同，只是阻塞原因从 "等待子进程" 变为 "等待用户回复"。

`AgentRunnerService` 已有暂停/恢复机制（`pausedSessions` Map），但那是 loop 级别的暂停（整个 loop 结束后暂停）。`ask_user` 是 loop 内部的暂停（单个 step 内的 tool 阻塞），两者的触发时机不同。

## Goals / Non-Goals

**Goals:**

- Main Agent 可以通过 ask_user 工具向用户提问
- 用户通过 API 回复后，tool resolve，LLM 继续执行
- 问题和回答持久化到 SQLite，防止 server 重启丢失
- 超时机制：用户长时间不回复时自动超时，agent 可以继续或停止
- Web UI 展示问题通知和回复界面（基础版本）

**Non-Goals:**

- 不实现自由文本消息注入（那是 message-injection change）
- 不实现 mo attach 中的问题回复（留给 mo-attach change）
- 不修改 agent loop 的 step 间消息检查（不需要）
- 不做问题优先级或排队（一次只有一个 agent 在运行）

## Decisions

### D1: ask_user 作为阻塞式 Tool 实现

```typescript
Tool.define({
  id: 'ask_user',
  description: 'Ask the user a question and wait for their reply',
  parameters: z.object({ question: z.string() }),
  execute: async ({ question }, context) => {
    // 1. 创建 question 记录（DB）
    // 2. emit question_asked 事件
    // 3. 返回 Promise，在用户回复后 resolve
    return new Promise<string>((resolve) => {
      pendingResolvers.set(questionId, resolve);
    });
  }
});
```

**可行性已验证**：
- Vercel AI SDK 的 `streamText` + `consumeStream()` 会等待所有 tool execute 完成
- 没有默认的 tool 超时（除非手动设置 `timeout.stepMs`）
- `spawn_coder` 已证明 30 分钟阻塞可行
- Node.js 事件循环不会被 Promise 阻塞（其他请求正常处理）

### D2: questions 表设计

```sql
CREATE TABLE questions (
  id TEXT PRIMARY KEY,
  issue_id TEXT NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
  question TEXT NOT NULL,
  answer TEXT,
  status TEXT NOT NULL DEFAULT 'pending',
  created_at TEXT NOT NULL,
  answered_at TEXT
);
```

状态：`pending` → `answered` | `expired`

### D3: 内存 resolver Map

`Map<string, { resolve: (answer: string) => void, timer: NodeJS.Timeout }>` 保存每个 pending question 的 Promise resolver 和超时定时器。

**替代方案**：用 DB 轮询。不采用——内存 Map 更简单，且单 agent 运行时不需要跨进程通信。

### D4: 超时机制

默认超时 24 小时（配置化）。超时后：
- question 状态设为 `expired`
- tool 返回 "No answer received within timeout. Proceed with your best judgment."
- 清理内存 resolver

用 `Promise.race` + `setTimeout` 实现。

### D5: API 设计

```
GET    /api/questions?issueId=xxx    — 列出某个 issue 的问题
GET    /api/questions/:id            — 获取单个问题详情
POST   /api/questions/:id/reply      — 回复问题 { answer: string }
POST   /api/questions/:id/expire     — 标记过期（可选，超时自动处理）
```

回复流程：
1. API handler 查找内存 resolver Map
2. 调用 `resolve(answer)`
3. 更新 DB（status=answered, answer=xxx, answered_at=now）
4. 清理定时器
5. emit `question_answered` 事件

### D6: System Prompt 更新

在 Main Agent 的 system prompt 中添加 ask_user 的使用指导：
- 何时使用（需求模糊、需要用户决策、发现潜在问题）
- 何时不使用（可以通过工具自行解决、有明确的最佳实践）
- 一次只问一个问题
- 问题要具体、可操作（不要问开放式问题）

### D7: Web UI 基础问题面板

在 IssueDetailPage 中添加问题区域：
- 监听 `question_asked` SSE 事件
- 显示 pending 问题列表
- 提供回复输入框和提交按钮
- 调用 `POST /api/questions/:id/reply`

不需要复杂的通知系统（toast、badge），只需在 Issue 详情页中展示。

### D8: ask_user 阻塞与 message-injection 的边界

当 agent 被 ask_user 阻塞时，session 状态仍为 active（不是 paused），`hasPausedSession()` 返回 false。此时 message-injection API（`POST /api/issues/:number/messages`）会返回 409。

这是预期行为，不需要特殊处理。两种交互方式在时序上互斥：

```
agent 运行中 (session active):
  - ask_user 可能触发 → Question Panel 可见
  - message-injection 不可用 → 409

agent gate 暂停 (session paused):
  - ask_user 不可能触发（agent 没在跑）
  - approve + message-injection 可用
```

用户在同一时刻只会看到 Question Panel 或 Gate+Message，不会同时出现。

### D9: Web UI 三状态布局

Issue 详情页按 agent 状态展示不同的交互区域：

```
agent 正在运行:
  Comments + (可选) Question Panel

agent gate 暂停:
  Comments + Gate Panel ([Approve] 按钮) + Message Input

agent 空闲 (done/closed):
  Comments (无交互区域)
```

Gate Panel 中 approve 按钮在上（主要动作），message input 在下（次要动作），上下排列。

## Risks / Trade-offs

- **[Risk] 用户永远不回复，agent 永久阻塞** → 超时机制（D4）解决。超时后 agent 收到默认回复可以自主决策
- **[Risk] Server 重启丢失内存 resolver** → DB 持久化（D2）+ 启动时恢复 pending questions 的提示。重启后 pending questions 仍在 DB 中，可以在 Web UI 显示。但 resolver 已丢失，agent session 也已丢失（sessions 不持久化），所以重启后实际上 agent 已经不在运行了。pending questions 可以标记为 expired
- **[Risk] ask_user 被滥用（agent 每步都问）** → System prompt 引导（D6）限制使用场景。后续可以添加 ask_user 频率限制
- **[Low] 单 agent 约束** → `AgentRunnerService` 已保证同时只有一个 agent 在运行，不存在并发问题
- **[Decided] ask_user 阻塞时 message-injection 返回 409** → 预期行为（D8）。两种交互时序互斥，Web UI 按状态切换显示
