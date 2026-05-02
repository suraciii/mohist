## MODIFIED Requirements

### Requirement: API 提供操作接口

Server SHALL 提供 RESTful API 供 CLI 执行操作，基于 Hono 框架实现。API handler SHALL 通过 IssueService 操作 issue 数据，不直接调用 StateManager 的 CRUD 方法。

The `GET /api/issues/:number/coder-sessions` endpoint SHALL return the `title` field for each coder session row. The `GET /api/agent/sessions` endpoint (which calls `findAllWithIssueInfo`) SHALL include the `title` column in its query and return it in the response.

#### Scenario: 创建 Issue
- **WHEN** CLI 请求 `POST /api/issues` with `{ title, body?, labels? }`
- **THEN** 通过 IssueService 创建 Issue
- **AND** 返回 Issue 信息

#### Scenario: 获取 coder sessions with title
- **WHEN** CLI 请求 `GET /api/issues/:number/coder-sessions`
- **THEN** 每个 coder session 对象包含 `title` 字段（string 或 null）

#### Scenario: 获取 agent sessions with title
- **WHEN** CLI 请求 `GET /api/agent/sessions`
- **THEN** 每个 session 对象包含 `title` 字段（string 或 null）
- **AND** `title` 值来自 `coder_session.title` 列
