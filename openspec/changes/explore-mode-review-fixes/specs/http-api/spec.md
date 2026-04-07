## MODIFIED Requirements

### Requirement: API 提供状态查询接口
Server SHALL 提供 RESTful API 供 CLI 查询状态，基于 Hono 框架实现。

#### Scenario: 获取全局状态
- **WHEN** CLI 请求 `GET /api/status`
- **THEN** 返回当前项目的 Issue 状态

#### Scenario: 获取所有项目状态
- **WHEN** CLI 请求 `GET /api/status?all=true`
- **THEN** 返回所有项目的 Issue 状态

#### Scenario: 获取单个 Issue 详情
- **WHEN** CLI 请求 `GET /api/issues/:number`
- **THEN** 返回指定 Issue 的详细信息

## ADDED Requirements

### Requirement: Explore messages 端点原子化持久化
`POST /api/explore/:id/messages` SHALL 在 agent 成功完成后才持久化用户消息和 assistant 消息。agent 运行失败时 SHALL 返回错误且不保留用户消息，避免孤立消息和重复消息。

#### Scenario: Agent 成功时持久化消息
- **WHEN** 用户发送消息到 `POST /api/explore/:id/messages`
- **AND** agent 成功完成流式响应
- **THEN** 用户消息和 assistant 消息同时持久化到数据库

#### Scenario: Agent 失败时不保留用户消息
- **WHEN** 用户发送消息到 `POST /api/explore/:id/messages`
- **AND** agent 运行过程中抛出错误
- **THEN** SSE 发送 `done` 事件包含 error 字段
- **AND** 用户消息不持久化到数据库

### Requirement: Explore messages 端点发送 issue number
`POST /api/explore/:id/messages` 的 SSE `done` 事件中 `issueId` 字段 SHALL 包含 issue number（数字），而非 issue id（UUID），以便前端正确导航到 issue 详情页。

#### Scenario: Agent 创建 issue 后发送正确 ID
- **WHEN** explore agent 调用 `create_issue` 工具成功
- **AND** SSE 发送 `done` 事件
- **THEN** `done` 事件中的 `issueId` 为 issue number（如 `"42"`）
- **AND** 前端可通过 `/issue/${issueId}` 正确导航到 issue 详情页
