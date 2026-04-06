## MODIFIED Requirements

### Requirement: API 提供项目管理接口

Server SHALL 提供项目管理的 RESTful API，基于 Hono 框架实现。API handler SHALL 通过 ProjectService 操作数据，不直接调用 StateManager 的 CRUD 方法。

#### Scenario: 列出项目
- **WHEN** CLI 请求 `GET /api/projects`
- **THEN** 返回所有已注册的项目列表

#### Scenario: 创建项目
- **WHEN** CLI 请求 `POST /api/projects` with `{ name, path }`
- **THEN** 通过 ProjectService 创建新项目
- **AND** 返回项目信息

#### Scenario: 删除项目
- **WHEN** CLI 请求 `DELETE /api/projects/:name`
- **THEN** 通过 ProjectService 从项目列表中移除项目

#### Scenario: 切换当前项目
- **WHEN** CLI 请求 `POST /api/projects/:name/use`
- **THEN** 通过 ProjectService 设置当前项目

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
- **THEN** 通过 IssueService 变更 issue stage
- **AND** Main Agent 被启动处理该 Issue
- **AND** 返回 Issue 信息和运行状态

## ADDED Requirements

### Requirement: API 路由接收 Service 实例

API 路由工厂函数 SHALL 接收 Service 实例而非 StateManager。`createIssueRoutes` 接收 IssueService 和 ProjectService，`createProjectRoutes` 接收 ProjectService。

#### Scenario: Issue 路由使用 Service
- **WHEN** server 启动并注册 issue 路由
- **THEN** `createIssueRoutes` 接收 `issueService` 和 `projectService` 参数
- **AND** handler 中不出现 `stateManager.createIssue`、`stateManager.updateIssueStage` 等直接 CRUD 调用

#### Scenario: Project 路由使用 Service
- **WHEN** server 启动并注册 project 路由
- **THEN** `createProjectRoutes` 接收 `projectService` 参数
- **AND** handler 中不出现 `stateManager.saveProject`、`stateManager.loadProjects` 等直接 CRUD 调用

### Requirement: StateManager 仅作为 repo 工厂

StateManager SHALL 仅提供 repo 实例的 getter 和数据库初始化，不暴露与 Service 重叠的 CRUD 方法。不保留 `createIssue`、`getIssueByNumber`、`updateIssueStage`、`updateIssueStatus`、`loadProjects`、`loadIssues`、`getProjectById`、`getProjectByName`、`saveProject`、`deleteProject`、`createComment`、`getCommentsByIssue`、`getLabels`、`getCurrentProjectId`、`setCurrentProjectId` 方法。

#### Scenario: StateManager 只暴露 repo getter
- **WHEN** 检查 StateManager 的公共方法
- **THEN** 仅包含 `getProjectRepo()`、`getIssueRepo()`、`getCommentRepo()`、`getConfigRepo()`、`getLabelRepo()` 以及 `isInitialized()`
- **AND** 不包含任何直接 CRUD 方法（createIssue、loadProjects 等）

#### Scenario: config 管理由 ProjectService 承担
- **WHEN** API 需要获取或设置当前项目
- **THEN** 通过 ProjectService 的 `getCurrent()` 和 `setCurrent()` 方法
- **AND** 不通过 StateManager 的 `getCurrentProjectId()` / `setCurrentProjectId()`

#### Scenario: ProjectService 提供 getCurrentId
- **WHEN** API 需要获取当前项目 ID（字符串）
- **THEN** 通过 ProjectService 的 `getCurrentId()` 方法
- **AND** 不通过 `projectService.getCurrent()?.id`

### Requirement: Status 和 Labels 路由使用 Service

`createStatusRoutes` SHALL 接收 ProjectService 和 IssueService。`createLabelRoutes` SHALL 接收 ProjectService。

#### Scenario: Status 路由使用 Service
- **WHEN** server 启动并注册 status 路由
- **THEN** `createStatusRoutes` 接收 `projectService` 和 `issueService` 参数
- **AND** handler 中不出现 `stateManager.loadProjects`、`stateManager.loadIssues`、`stateManager.getProjectById`、`stateManager.getCurrentProjectId`

#### Scenario: Labels 路由使用 Service
- **WHEN** server 启动并注册 labels 路由
- **THEN** `createLabelRoutes` 接收 `projectService` 参数
- **AND** handler 中不出现 `stateManager.getCurrentProjectId`、`stateManager.getLabels`
