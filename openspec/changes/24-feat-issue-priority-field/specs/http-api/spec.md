## MODIFIED Requirements

### Requirement: API 提供操作接口

Server SHALL 提供 RESTful API 供 CLI 执行操作，基于 Hono 框架实现。API handler SHALL 通过 IssueService 操作 issue 数据，不直接调用 StateManager 的 CRUD 方法。

#### Scenario: 创建 Issue
- **WHEN** CLI 请求 `POST /api/issues` with `{ title, body?, labels?, priority? }`
- **THEN** 通过 IssueService 创建 Issue
- **AND** Issue 的 priority 为请求中的值，未指定时默认 `p2`
- **AND** 返回 Issue 信息（包含 priority）

#### Scenario: 创建 Issue 时指定无效 priority
- **WHEN** CLI 请求 `POST /api/issues` with `{ title, priority: "urgent" }`
- **THEN** 返回 400 错误
- **AND** 错误信息包含 "Invalid priority"

#### Scenario: 更新 Issue
- **WHEN** CLI 请求 `PATCH /api/issues/:number` with `{ title?, body?, addLabels?, removeLabels?, priority? }`
- **THEN** 通过 IssueService 更新 Issue
- **AND** 如果提供 priority，更新 Issue 的 priority
- **AND** 返回更新后的 Issue（包含 priority）

#### Scenario: 更新 Issue 时指定无效 priority
- **WHEN** CLI 请求 `PATCH /api/issues/:number` with `{ priority: "high" }`
- **THEN** 返回 400 错误
- **AND** 错误信息包含 "Invalid priority"

#### Scenario: 添加评论
- **WHEN** CLI 请求 `POST /api/issues/:number/comments` with `{ body }`
- **THEN** 通过 IssueService 创建 comment
- **AND** 返回 comment 信息

#### Scenario: 启动 Issue 处理
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **THEN** Main Agent 被启动处理该 Issue
- **AND** 返回 Issue 信息和运行状态

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
- **THEN** 返回指定 Issue 的详细信息（包含 priority 字段）

#### Scenario: 按 priority 筛选 Issues
- **WHEN** CLI 请求 `GET /api/issues?priority=p0`
- **THEN** 返回 priority 为 `p0` 的 Issues
- **AND** 按 priority ASC、number ASC 排序

#### Scenario: Issues 默认排序
- **WHEN** CLI 请求 `GET /api/issues`（不指定 priority）
- **THEN** 返回所有 Issues
- **AND** 按 priority ASC、number ASC 排序
