## ADDED Requirements

### Requirement: API 列出已注册 skills

Server SHALL 提供 `GET /api/skills` 端点，返回当前项目的所有已注册 skills。

#### Scenario: 列出 skills

- **WHEN** 请求 `GET /api/skills`
- **THEN** 返回 200，body 为 skills 数组
- **AND** 每个 skill 包含 `id`、`name`、`description`、`createdAt` 字段

#### Scenario: 无 skills

- **WHEN** 请求 `GET /api/skills`
- **AND** 当前项目无已注册 skill
- **THEN** 返回 200，body 为空数组

#### Scenario: 无项目上下文

- **WHEN** 请求 `GET /api/skills`
- **AND** 无当前 project 上下文
- **THEN** 返回 400 错误

### Requirement: API 触发 skill 执行

Server SHALL 提供 `POST /api/skills/:name/run` 端点，触发指定 skill 的手动执行。

#### Scenario: 成功触发

- **WHEN** 请求 `POST /api/skills/analyze-codebase/run`
- **AND** skill `analyze-codebase` 存在
- **THEN** 返回 202 Accepted
- **AND** body 包含 `runId`、`status: "running"`、`skillName`

#### Scenario: skill 不存在

- **WHEN** 请求 `POST /api/skills/nonexistent/run`
- **AND** skill `nonexistent` 不存在
- **THEN** 返回 404 错误

#### Scenario: 无项目上下文

- **WHEN** 请求 `POST /api/skills/:name/run`
- **AND** 无当前 project 上下文
- **THEN** 返回 400 错误

### Requirement: API 查询 skill 执行历史

Server SHALL 提供 `GET /api/skills/:name/runs` 端点，返回指定 skill 的执行历史。

#### Scenario: 查询执行历史

- **WHEN** 请求 `GET /api/skills/analyze-codebase/runs`
- **AND** skill `analyze-codebase` 存在
- **THEN** 返回 200，body 为 run 记录数组（按时间降序）
- **AND** 每条记录包含 `id`、`status`、`output`（截断至 500 字符）、`error`、`issueId`、`startedAt`、`completedAt`

#### Scenario: skill 不存在时查询历史

- **WHEN** 请求 `GET /api/skills/nonexistent/runs`
- **THEN** 返回 404 错误
