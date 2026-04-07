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

### D2: question_asked 事件包含 questionId

`question_asked` 事件 payload 已包含 `questionId`（`ask-user.ts:75-80`），无需修改。mo attach 从事件数据中提取 questionId 用于回复 API。

### D3: 不新增事件类型

复用现有 `question_asked` 和 `question_answered` 事件。mo attach 的 `question_answered` 监听用于清理 QUESTION_MODE 状态。

## Risks / Trade-offs

- [Risk] 用户在 QUESTION_MODE 输入的内容被当作问题回答，而非自由文本 → 缓解：提示文本明确说明 "Agent is asking a question"，question_asked 事件打印具体问题内容，用户有足够上下文判断
- [Risk] 回答时 question 已过期（24h timeout 或 agent 已结束）→ 缓解：API 返回 409/410，attach 显示错误信息并回到 IDLE
- [Risk] 两种模式切换过快（agent resume 后立刻又 ask_user）→ 缓解：事件处理是顺序的，状态机清晰，不会丢失事件
