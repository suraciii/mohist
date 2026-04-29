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
- **THEN** Main Agent 被启动处理该 Issue
- **AND** 返回 Issue 信息和运行状态

#### Scenario: Rebase 冲突自动解决成功
- **WHEN** CLI 请求 `POST /api/issues/:number/rebase`
- **AND** issue stage 为 Plan/Build/Review
- **AND** rebase 遇到冲突且 `resolveConflicts` 回调已注入
- **AND** coder agent 成功解决冲突
- **THEN** 返回 200 `{ success: true, data: { rebased: true, autoResolved: true, message: "..." } }`
- **AND** 响应 data 包含 `autoResolved: true` 标识

#### Scenario: Rebase 冲突自动解决失败降级
- **WHEN** CLI 请求 `POST /api/issues/:number/rebase`
- **AND** rebase 遇到冲突且 coder agent 解决失败
- **THEN** 返回 409 `{ success: false, error: "...", data: { rebased: false, conflicts: [...], autoResolved: false } }`

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

#### Scenario: Issue 路由接收 resolveConflicts 回调
- **WHEN** server 启动并注册 issue 路由
- **THEN** `createIssueRoutes` 接收可选的 `resolveConflicts` 回调参数
- **AND** 回调签名接受 `(issue, worktreePath, conflictFiles)` 并返回 `{ success: boolean, error?: string }`
