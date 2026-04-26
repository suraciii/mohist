## MODIFIED Requirements

### Requirement: Agent status API 返回可恢复 issues

`GET /api/agent/status` 返回值 SHALL 包含以下字段：
- `running` (boolean) — 是否有任何 agent 在运行
- `activeAgents` (array) — 所有运行中 agent 列表，每项含 `{ issueId, issueNumber, projectId }`
- `maxConcurrentAgents` (number) — 配置的并发上限
- `queueDepth` (number) — 队列深度
- `recoverableIssues` (array) — 可恢复的 issue 列表，每项含 `{ issueNumber, stage }`
- `waitingQuestions` (array) — 等待回答的问题列表

#### Scenario: Server 重启后检测可恢复 issues
- **WHEN** server 重启
- **AND** 数据库中存在 `status = 'active'` 且 `stage` 不是 `draft` 的 issues
- **THEN** `GET /api/agent/status` 返回的 `recoverableIssues` 数组包含这些 issue 的 number 和 stage

#### Scenario: 所有 issue 正常完成时无可恢复项
- **WHEN** 所有 issue 的 status 都不是 `active`
- **THEN** `GET /api/agent/status` 返回的 `recoverableIssues` 为空数组

#### Scenario: 多 agent 并行运行时返回完整 activeAgents
- **WHEN** agent 正在运行 issue #3 和 issue #7
- **THEN** `GET /api/agent/status` 返回 `activeAgents` 数组包含两个条目
- **AND** `activeAgents[0].issueNumber` 为 3
- **AND** `activeAgents[1].issueNumber` 为 7
- **AND** `maxConcurrentAgents` 为配置值（如 8）
- **AND** `running` 为 true

#### Scenario: 无 agent 运行时
- **WHEN** 没有任何 agent 在运行
- **THEN** `activeAgents` 为空数组
- **AND** `running` 为 false
- **AND** `maxConcurrentAgents` 仍返回配置值

### Requirement: Start handler 校验 issue status

`POST /api/issues/:number/start` SHALL 在执行前校验 issue status 和并发上限，超限时返回 429。

#### Scenario: Start blocked issue
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue status 为 `blocked`
- **THEN** server 返回 400 错误
- **AND** 错误信息包含 "blocked"

#### Scenario: Start active issue in draft stage
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue status 为 `active` 且 stage 为 `draft`
- **AND** 并发上限未达到
- **THEN** 正常启动 agent

#### Scenario: Start when concurrent limit reached
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** `activeAgents.size >= maxConcurrentAgents`
- **THEN** server 返回 429 错误
- **AND** 错误信息包含并发上限信息（如 "Concurrent agent limit reached (8)"）

#### Scenario: Start issue already running
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** 该 issue 已有 agent 在运行
- **THEN** server 返回 409 Conflict
- **AND** 错误信息包含 "already has an agent running"

#### Scenario: Propose when concurrent limit reached
- **WHEN** CLI 请求 `POST /api/issues/:number/propose`
- **AND** `activeAgents.size >= maxConcurrentAgents`
- **THEN** server 返回 429 错误
- **AND** 错误信息包含并发上限信息
