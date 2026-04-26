## MODIFIED Requirements

### Requirement: CLI 支持本地 Issue CRUD

CLI SHALL 通过 Server API 支持本地 Issue 的创建、读取、更新、删除操作。

#### Scenario: CLI 调用 Server API 创建 Issue
- **WHEN** 用户执行 `mo issue create "title" --priority p1`
- **THEN** CLI 发送 POST /api/issues 请求到 Server，body 包含 `{ title, priority: "p1" }`
- **AND** Server 在本地 SQLite 创建 Issue
- **AND** CLI 显示创建结果（包含 priority）

#### Scenario: CLI 创建 Issue 不指定 priority
- **WHEN** 用户执行 `mo issue create "title"`（不带 `--priority`）
- **THEN** CLI 发送 POST /api/issues 请求到 Server，body 不包含 priority
- **AND** Server 使用默认 priority `p2`

#### Scenario: CLI 调用 Server API 列出 Issues
- **WHEN** 用户执行 `mo issue list`
- **THEN** CLI 发送 GET /api/issues 请求到 Server
- **AND** Server 从本地 SQLite 查询 Issues
- **AND** CLI 格式化显示结果，包含 priority 列

#### Scenario: CLI 按 priority 筛选 Issues
- **WHEN** 用户执行 `mo issue list --priority p0`
- **THEN** CLI 发送 GET /api/issues?priority=p0 请求到 Server
- **AND** CLI 只显示 priority 为 `p0` 的 Issues

#### Scenario: CLI 调用 Server API 更新 Issue
- **WHEN** 用户执行 `mo issue update <id> --title "new"`
- **THEN** CLI 发送 PATCH /api/issues/:id 请求到 Server
- **AND** Server 更新本地 SQLite
- **AND** CLI 显示更新结果

#### Scenario: CLI 更新 Issue priority
- **WHEN** 用户执行 `mo issue update <id> --priority p0`
- **THEN** CLI 发送 PATCH /api/issues/:id 请求到 Server，body 包含 `{ priority: "p0" }`
- **AND** Server 更新 Issue 的 priority
- **AND** CLI 显示更新结果

#### Scenario: CLI 调用 Server API 添加评论
- **WHEN** 用户执行 `mo issue comment <id> "text"`
- **THEN** CLI 发送 POST /api/issues/:id/comments 请求到 Server
- **AND** Server 在本地 SQLite 创建 comment
- **AND** CLI 显示成功消息

### Requirement: Server API 扩展

Server SHALL 新增以下 API 端点支持本地 Issue CRUD。

#### Scenario: POST /api/issues
- **WHEN** Server 收到 POST /api/issues 请求
- **WITH** body: { title, body?, labels?, priority? }
- **THEN** Server 在当前项目创建 Issue
- **AND** Issue priority 为请求中的值或默认 `p2`
- **AND** 返回 Issue 详情（包含 priority）

#### Scenario: PATCH /api/issues/:id
- **WHEN** Server 收到 PATCH /api/issues/:id 请求
- **WITH** body: { title?, body?, labels?, priority? }
- **THEN** Server 更新指定 Issue
- **AND** 如果提供 priority，更新 Issue 的 priority
- **AND** 返回更新后的 Issue

#### Scenario: GET /api/issues with priority filter
- **WHEN** Server 收到 GET /api/issues?priority=p1 请求
- **THEN** Server 返回 priority 为 `p1` 的 Issues
- **AND** 按 priority ASC、number ASC 排序

#### Scenario: POST /api/issues/:id/comments
- **WHEN** Server 收到 POST /api/issues/:id/comments 请求
- **WITH** body: { body }
- **THEN** Server 创建 comment
- **AND** 返回 comment 详情

#### Scenario: GET /api/labels
- **WHEN** Server 收到 GET /api/labels 请求
- **THEN** Server 返回当前项目所有使用过的 labels
- **AND** 按名称排序
