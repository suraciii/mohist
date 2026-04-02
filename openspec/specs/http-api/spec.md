## Requirements

### Requirement: API 提供项目管理接口

Server SHALL 提供项目管理的 RESTful API。

#### Scenario: 列出项目
- **WHEN** CLI 请求 `GET /api/projects`
- **THEN** 返回所有已注册的项目列表

#### Scenario: 创建项目
- **WHEN** CLI 请求 `POST /api/projects` with `{ name, repo }`
- **THEN** 创建新项目
- **AND** 返回项目信息

#### Scenario: 删除项目
- **WHEN** CLI 请求 `DELETE /api/projects/:name`
- **THEN** 从项目列表中移除项目

#### Scenario: 切换当前项目
- **WHEN** CLI 请求 `POST /api/projects/:name/use`
- **THEN** 设置当前项目

### Requirement: API 提供状态查询接口

Server SHALL 提供 RESTful API 供 CLI 查询状态。

#### Scenario: 获取全局状态
- **WHEN** CLI 请求 `GET /api/status`
- **THEN** 返回当前项目的 Issue 状态

#### Scenario: 获取所有项目状态
- **WHEN** CLI 请求 `GET /api/status?all=true`
- **THEN** 返回所有项目的 Issue 状态

#### Scenario: 获取单个 Issue 详情
- **WHEN** CLI 请求 `GET /api/issues/:number`
- **THEN** 返回指定 Issue 的详细信息

### Requirement: API 提供操作接口

Server SHALL 提供 RESTful API 供 CLI 执行操作。

#### Scenario: 启动 Issue 处理
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **THEN** Main Agent 被启动处理该 Issue
- **AND** 返回 Issue 信息和运行状态

### Requirement: API 提供配置接口

Server SHALL 提供配置管理的 RESTful API。

#### Scenario: 获取配置
- **WHEN** CLI 请求 `GET /api/config`
- **THEN** 返回当前配置（隐藏敏感信息）

#### Scenario: 设置配置
- **WHEN** CLI 请求 `PUT /api/config/:key` with `{ value }`
- **THEN** 更新配置值

### Requirement: API 处理错误情况

Server SHALL 返回清晰的错误响应。

#### Scenario: Server 未运行时
- **WHEN** CLI 请求任何 API
- **AND** server 未运行
- **THEN** 连接被拒绝（CLI 处理此错误）

#### Scenario: Issue 不存在
- **WHEN** 请求的 Issue ID 不存在
- **THEN** 返回 404 错误
- **AND** 包含错误信息 "Issue not found"

#### Scenario: Server 内部错误
- **WHEN** server 发生内部错误
- **THEN** 返回 500 错误
- **AND** 记录错误日志

### Requirement: Status API reflects M1 stage model
The status API SHALL only report stages used in M1: draft, designing, implementing, done. The response SHALL NOT include task-related fields (runningTasks, queuedTasks, activeWorkers) or waiting-stage counts (waitingDesignReview, waitingReview). The `ServerState` interface SHALL NOT contain `activeTasks` or `queuedTasks` fields.

#### Scenario: Get current project status
- **WHEN** CLI 请求 `GET /api/status`
- **THEN** the response SHALL include `issuesByStage` with only `draft`, `designing`, `implementing`, `done` counts
- **AND** the response SHALL NOT include `runningTasks`, `queuedTasks`, or `activeWorkers`

#### Scenario: ServerState has no task fields
- **WHEN** the ServerState interface is inspected
- **THEN** it SHALL NOT contain `activeTasks` or `queuedTasks`

#### Scenario: Issue show endpoint omits stale fields
- **WHEN** CLI 请求 `GET /api/issues/:number`
- **THEN** the response SHALL NOT include `progress` or `stageInfo` fields
- **AND** the issue's current stage SHALL still be available in `issue.stage`

### Requirement: Removed endpoints return 404
Endpoints that are removed (approve, resume, pause) SHALL return HTTP 404 instead of their previous behavior.

#### Scenario: Removed endpoint accessed
- **WHEN** a request is made to `POST /api/issues/:number/approve`, `POST /api/issues/:number/resume`, or `POST /api/issues/:number/pause`
- **THEN** the response SHALL have status code 404

### Requirement: Start handler 校验 issue status

`POST /api/issues/:number/start` SHALL 在执行前校验 issue status，blocked 的 issue 不允许 start。

#### Scenario: Start blocked issue
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue status 为 `blocked`
- **THEN** server 返回 400 错误
- **AND** 错误信息包含 "blocked"

#### Scenario: Start active issue in draft stage
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue status 为 `active` 且 stage 为 `draft`
- **THEN** 正常启动 agent

### Requirement: 无 project 上下文时拒绝创建 issue

`POST /api/issues` SHALL 在无当前 project 时返回错误。

#### Scenario: 无 project 上下文创建 issue
- **WHEN** CLI 请求 `POST /api/issues`
- **AND** server 无当前 project 上下文（`getCurrentProjectId()` 返回 null）
- **THEN** server 返回 400 错误
- **AND** 错误信息包含 "no active project"

#### Scenario: 有 project 上下文创建 issue
- **WHEN** CLI 请求 `POST /api/issues`
- **AND** server 有当前 project 上下文
- **THEN** 正常创建 issue
