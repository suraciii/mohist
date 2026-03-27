## ADDED Requirements

### Requirement: API 提供问题交互接口
Server SHALL 提供 RESTful API 用于问题交互（ask_user 工具）。

#### Scenario: 列出待回答问题
- **WHEN** 请求 `GET /api/questions`
- **THEN** 返回当前所有待回答的问题列表

#### Scenario: 回答问题
- **WHEN** 请求 `POST /api/questions/:requestId/reply` with `{ answers }`
- **THEN** 解除对应问题的阻塞，agent loop 继续

#### Scenario: 拒绝问题
- **WHEN** 请求 `POST /api/questions/:requestId/reject`
- **THEN** 对应问题的 Deferred 以 RejectedError reject
- **AND** agent loop 收到 rejection 并处理

### Requirement: API 提供事件订阅接口
Server SHALL 提供 SSE 端点供 CLI channel 订阅 issue 事件。

#### Scenario: 订阅 issue 事件
- **WHEN** 请求 `GET /api/issues/:issueId/events` (SSE)
- **THEN** server 以 SSE 格式推送该 issue 的所有事件
- **AND** 连接断开后自动清理

### Requirement: API 提供用户消息接口
Server SHALL 提供 RESTful API 用于注入用户消息到 agent session。

#### Scenario: 发送用户消息
- **WHEN** 请求 `POST /api/issues/:issueId/messages` with `{ content }`
- **THEN** 消息被注入到该 issue 的 Main Agent session
- **AND** Main Agent LLM 被触发处理新消息
