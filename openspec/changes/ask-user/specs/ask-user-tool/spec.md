## NEW Requirements

### Capability: ask-user-tool

Main Agent 可以调用 ask_user 工具向用户提问，阻塞等待回复后继续执行。

#### Requirement: ask_user 工具阻塞等待用户回复

ask_user 工具 SHALL 接受一个 question 字符串参数，创建问题记录，emit 事件，然后阻塞直到用户通过 API 回复或超时。

##### Scenario: agent 提问并收到回复
- **WHEN** Main Agent 调用 ask_user("这个 API 应该返回 JSON 还是 XML？")
- **THEN** 一条 pending 问题记录创建到 questions 表
- **AND** EventBus emit `question_asked` 事件
- **AND** 工具执行阻塞，不返回给 LLM
- **WHEN** 用户通过 API 回复 "JSON"
- **THEN** 工具返回 "用户回答: JSON" 给 LLM
- **AND** LLM 继续执行下一步

##### Scenario: 用户超时未回复
- **WHEN** Main Agent 调用 ask_user
- **AND** 用户在超时时间内未回复
- **THEN** 问题状态设为 `expired`
- **AND** 工具返回 "No answer received within timeout. Proceed with your best judgment."
- **AND** LLM 可以继续执行或选择停止

#### Requirement: 问题和回答持久化

所有问题和回答 SHALL 存储在 questions 表中，包含 issue_id、question text、answer text、status 和时间戳。

##### Scenario: server 重启后问题可查
- **WHEN** server 重启
- **THEN** 之前创建的 questions 记录仍然存在
- **AND** pending 状态的问题可以被查询到

### Capability: question-api

HTTP API 管理问题和回答。

#### Requirement: 列出 issue 的问题

API SHALL 提供 `GET /api/questions?issueId=xxx` 端点，返回指定 issue 的所有问题，按创建时间降序排列。

#### Requirement: 回复问题

API SHALL 提供 `POST /api/questions/:id/reply` 端点，接受 `{ answer: string }` 参数。回复后更新问题状态为 answered，并触发 ask_user 工具的 Promise resolve。

##### Scenario: 回复后 agent 继续执行
- **WHEN** POST /api/questions/:id/reply { answer: "JSON" }
- **THEN** 问题状态更新为 answered
- **AND** ask_user 工具的阻塞 Promise 被 resolve，返回 "用户回答: JSON"
- **AND** EventBus emit `question_answered` 事件
