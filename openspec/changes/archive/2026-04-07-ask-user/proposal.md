## Why

当前 Main Agent 只能在预定义的 gate 点（build 完成后、check 完成后）暂停等待用户审批。Agent 无法在执行过程中主动向用户提问——比如 "这个 API 应该返回 JSON 还是 XML？" 或 "我发现了两个可能的实现方案，你倾向哪个？"。

这限制了 agent 的自主性。如果 agent 遇到模糊需求或需要用户决策，它只能自己猜测或直接继续，可能导致返工。

`ask_user` 工具让 agent 可以在任意时刻暂停执行，向用户提出问题，等待回复后继续。这是 vision 里 "随时介入" 的核心能力。

## What Changes

- 新增 `ask_user` 工具，注册到 Main Agent 的 ToolRegistry
- 工具的 execute 返回一个 Promise，在用户回复前阻塞（利用 Vercel AI SDK 无工具超时的特性）
- 新增 `questions` 表（SQLite），持久化问题和回答
- 新增 EventBus 事件类型（`question_asked`、`question_answered`）
- 新增 Question API（CRUD + reply endpoint）
- Main Agent system prompt 更新，说明 ask_user 的用法

## Capabilities

### New Capabilities

- `ask-user-tool`: Main Agent 可以调用 ask_user 工具向用户提问，工具阻塞直到用户回复。回复后 tool 返回用户答案，LLM 继续决策。
- `question-api`: HTTP API 管理问题和回答，包括列表、回复、过期处理

### Modified Capabilities

- `agent-runtime`: Main Agent 的 tool 集中新增 ask_user 工具
- `event-bus`: 新增 `question_asked` 和 `question_answered` 事件类型
- `web-ui`: Web UI 接收问题通知并展示问题弹窗/面板（基础版本）

## Impact

- `tools/ask-user.ts`: 新增 ask_user 工具实现
- `agents/main-agent.ts`: 注册 ask_user 工具，更新 system prompt
- `db/schema.ts`: 新增 questions 表（migration v6）
- `db/question-repo.ts`: 新增 repo
- `services/event-bus.ts`: 新增事件类型
- `api/questions.ts`: 新增 API 路由
- `api/events.ts`: ALL_EVENT_TYPES 添加新事件
- `server/index.ts`: 创建 QuestionRepo，注入到 tool 和 API
