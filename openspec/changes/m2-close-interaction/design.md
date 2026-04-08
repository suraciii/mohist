## Context

mohist 的 `mo attach` 命令通过 SSE 订阅 agent 事件，提供实时监控和交互能力。当前只处理 `agent_paused` 事件（gate 暂停），用户输入通过 `POST /issues/:number/messages` 注入 Main Agent session。

同时，`ask_user` 工具允许 Main Agent 向用户提问并阻塞等待回答，通过 `POST /questions/:id/reply` API 回答。但 CLI 用户没有命令行入口来回答问题——`mo attach` 在 ask_user 阻塞时不显示输入提示。

Main Agent loop 是单线程的，gate 暂停（loop 结束，session pause）和 ask_user 阻塞（loop 活着，Promise 挂起）在时间上互斥，不会同时发生。

## Goals / Non-Goals

**Goals:**
- mo attach 同时处理 gate 暂停和 ask_user 阻塞，用户无需区分两种模式
- 更新 backlog 标记 B-032、B-033 为已完成

**Non-Goals:**
- 不修改服务端 API 或事件机制
- 不实现多 Issue 并发交互（未来 M4）
- 不修改 ask_user 工具本身的行为

## Decisions

### D1: mo attach 智能路由用户输入

mo attach 维护一个交互状态（IDLE / GATE_MODE / QUESTION_MODE），根据 SSE 事件自动切换：

- `agent_paused` 事件 → GATE_MODE，用户输入 → `POST /issues/:number/messages`
- `question_asked` 事件 → QUESTION_MODE，用户输入 → `POST /questions/:id/reply`
- `agent_started` / `agent_completed` / `question_answered` → IDLE

用户只看到提示符和上下文说明，不需要知道底层模式。

**替代方案**: 保持分离，新增 `mo question reply` 命令。放弃原因：用户在 attach 终端看到问题却要另开终端回答，体验割裂。

**实现优化**: 使用单一状态变量替代两个独立变量（pausedIssue + waitingQuestion），通过 TypeScript 类型系统保证互斥性：

```typescript
type InteractionState = 
  | { type: 'IDLE' }
  | { type: 'GATE_MODE'; issueId: string; issueNumber: number; lastMessage: string | null }
  | { type: 'QUESTION_MODE'; questionId: string; question: string; issueId: string };
```

优势：
- 单一清理点：`state = { type: 'IDLE' }`
- 编译期保证不会同时处于两种模式
- 状态转换逻辑集中，避免遗漏

### D2: question_asked 事件包含 questionId

`question_asked` 事件 payload 已包含 `questionId`（`ask-user.ts:75-80`），无需修改。mo attach 从事件数据中提取 questionId 用于回复 API。

### D3: 不新增事件类型

复用现有 `question_asked` 和 `question_answered` 事件。mo attach 的 `question_answered` 监听用于清理 QUESTION_MODE 状态。

### D4: QUESTION_MODE 视觉区分

QUESTION_MODE 需要有强烈的视觉提示，让用户明确知道当前是在**回答问题**而非发送自由消息：

```
┌─────────────────────────────────────────────────────────────┐
│  [Question] Agent is asking for issue #123:                  │
│                                                              │
│  "Which approach do you prefer: A or B?"                     │
│                                                              │
│  Type your answer below, or 'quit' to detach:               │
└─────────────────────────────────────────────────────────────┘
> 
```

GATE_MODE 保持简洁提示：
```
Agent paused for issue #123. Type a message to send, or 'quit' to detach.
> 
```

### D5: QUESTION_MODE 时 quit 的行为

用户在 QUESTION_MODE 输入 `quit` 或 `exit`：

- **行为**: 正常退出 attach（不回答问题）
- **警告**: 显示提示信息告知用户 agent 会继续等待
  ```
  Warning: Quitting without answering. The agent will wait 24h for timeout.
  Use 'mo question reply <id>' later to answer, or let it timeout.
  ```

**替代方案**: 实现 cancel API 来主动取消问题。放弃原因：当前 API 没有 cancel 端点，且用户随时可以重新 attach 回答问题。

## Risks / Trade-offs

- [Risk] 用户在 QUESTION_MODE 输入的内容被当作问题回答，而非自由文本 → 缓解：提示文本明确说明 "Agent is asking a question"，question_asked 事件打印具体问题内容，用户有足够上下文判断
- [Risk] 回答时 question 已过期（24h timeout 或 agent 已结束）→ 缓解：API 返回 409/410，attach 显示错误信息并回到 IDLE
- [Risk] 两种模式切换过快（agent resume 后立刻又 ask_user）→ 缓解：事件处理是顺序的，状态机清晰，不会丢失事件
- [Risk] 状态变量不一致（pausedIssue 和 waitingQuestion 同时非空）→ 缓解：使用单一 InteractionState 变量，类型系统保证互斥性
- [Risk] 代码复杂度增长（attach.ts 将超过 280 行）→ 缓解：保持当前单文件结构，通过清晰的代码组织和注释控制复杂度；未来若继续增长再考虑模块拆分
