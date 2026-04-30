## ADDED Requirements

### Requirement: Worktree-using endpoints SHALL reject when project not found

所有使用 worktree 的 issue 操作 endpoint（`POST /api/issues/:number/start`、`POST /api/issues/:number/reopen`、`POST /api/issues/:number/approve`、`POST /api/issues/:number/reject`、`POST /api/issues/:number/messages`）SHALL 在 `projectService.getById(projectId)` 返回 null 时返回 HTTP 404 错误，而非静默使用 `process.cwd()` 作为 worktreePath。SHALL 记录 warn 级别日志。

#### Scenario: start endpoint project 为 null

- **WHEN** 请求 `POST /api/issues/:number/start`
- **AND** `projectService.getById(projectId)` 返回 null
- **THEN** 返回 HTTP 404
- **AND** 响应 body 包含 `{ success: false, error: "Project not found" }`
- **AND** 不启动 agent、不执行 stage transition
- **AND** 记录 warn 日志，包含 projectId 和 issue number

#### Scenario: reopen endpoint project 为 null

- **WHEN** 请求 `POST /api/issues/:number/reopen`
- **AND** `projectService.getById(projectId)` 返回 null
- **THEN** 返回 HTTP 404
- **AND** 响应 body 包含 `{ success: false, error: "Project not found" }`
- **AND** 记录 warn 日志

#### Scenario: approve endpoint project 为 null

- **WHEN** 请求 `POST /api/issues/:number/approve`
- **AND** `projectService.getById(projectId)` 返回 null
- **THEN** 返回 HTTP 404
- **AND** 响应 body 包含 `{ success: false, error: "Project not found" }`
- **AND** 记录 warn 日志

#### Scenario: reject endpoint project 为 null

- **WHEN** 请求 `POST /api/issues/:number/reject`
- **AND** `projectService.getById(projectId)` 返回 null
- **THEN** 返回 HTTP 404
- **AND** 响应 body 包含 `{ success: false, error: "Project not found" }`
- **AND** 记录 warn 日志

#### Scenario: messages endpoint project 为 null

- **WHEN** 请求 `POST /api/issues/:number/messages`
- **AND** `projectService.getById(projectId)` 返回 null
- **THEN** 返回 HTTP 404
- **AND** 响应 body 包含 `{ success: false, error: "Project not found" }`
- **AND** 记录 warn 日志

## MODIFIED Requirements

### Requirement: API 提供操作接口

Server SHALL 提供 RESTful API 供 CLI 执行操作，基于 Hono 框架实现。API handler SHALL 通过 IssueService 操作 issue 数据，不直接调用 StateManager 的 CRUD 方法。

#### Scenario: 创建 Issue
- **WHEN** CLI 请求 `POST /api/issues` with `{ title, body?, labels? }`
- **THEN** 通过 IssueService 创建 Issue
- **AND** 返回 Issue 信息

#### Scenario: 更新 Issue
- **WHEN** CLI 请求 `PATCH /api/issues/:number` with `{ title?, body?, addLabels?, removeLabels? }`
- **THEN** 通过 IssueService 更新 Issue
- **AND** 返回更新后的 Issue

#### Scenario: 添加评论
- **WHEN** CLI 请求 `POST /api/issues/:number/comments` with `{ body }`
- **THEN** 通过 IssueService 创建 comment
- **AND** 返回 comment 信息

#### Scenario: 启动 Issue 处理
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** project 存在
- **THEN** Main Agent 被启动处理该 Issue，使用隔离的 worktree 路径
- **AND** 返回 Issue 信息和运行状态

### Requirement: API 支持自由文本消息注入

Server SHALL 提供 `POST /api/issues/:number/messages` 端点，允许用户在 agent 暂停时注入自由文本消息到 agent session。

#### Scenario: 注入消息并恢复 agent
- **WHEN** agent 已暂停（gate 审批点，session status 为 paused）
- **AND** 用户 POST `POST /api/issues/:number/messages` with `{ message: "改用 PostgreSQL" }`
- **AND** project 存在
- **THEN** 消息被追加到 agent session
- **AND** agent 自动 resume，开始新的 LLM loop
- **AND** 返回 200

#### Scenario: agent 未暂停时拒绝注入
- **WHEN** agent 正在运行（包括 ask_user 阻塞状态，session status 为 active）
- **AND** 用户 POST `POST /api/issues/:number/messages`
- **THEN** 返回 409 Conflict
- **AND** 错误信息 "Agent is not paused for issue #N"
