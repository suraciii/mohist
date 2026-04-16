## ADDED Requirements

### Requirement: Initial message injection on empty session

当 `runAgentLoop` 收到的 session messages 为空数组时，系统 SHALL 在调用 AI SDK 之前通过 `sessionManager.appendMessage()` 自动注入一条 `role: 'user'` 的初始消息，内容为引导 agent 开始工作的通用指令。

#### Scenario: New session with empty messages

- **WHEN** `runAgentLoop` 被调用且 `session.messages` 为空数组
- **THEN** 系统通过 `sessionManager.appendMessage(session.id, { role: 'user', content: '...' })` 注入初始消息
- **THEN** `streamText` 收到非空的 messages 数组，调用正常完成，不抛出 `InvalidPromptError`

#### Scenario: Session with existing messages

- **WHEN** `runAgentLoop` 被调用且 `session.messages` 非空
- **THEN** 系统不注入任何额外消息，行为与修改前一致

### Requirement: Injected message content

注入的初始消息内容 SHALL 引导 agent 自主开始工作，包含对当前 issue 的基本上下文引用。

#### Scenario: Injected message references current issue

- **WHEN** 系统注入初始消息
- **THEN** 消息内容包含让 agent 开始处理当前 issue 并读取 workflow 配置的指令
- **THEN** 注入的消息被记录到 session 的 messages 数组中，后续 resume 时可见
