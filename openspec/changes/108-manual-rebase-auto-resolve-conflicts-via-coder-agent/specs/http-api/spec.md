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

## ADDED Requirements

### Requirement: Rebase 端点冲突时返回 202

`POST /api/issues/:number/rebase` 在 Plan/Build/Review 阶段遇到冲突时 SHALL 返回 HTTP 202 Accepted，表示冲突解决已异步启动。

#### Scenario: 冲突时返回 202
- **WHEN** 请求 `POST /api/issues/:number/rebase`
- **AND** issue stage 为 Plan、Build 或 Review
- **AND** rebase 产生冲突
- **THEN** 返回 HTTP 202
- **AND** body 为 `{ success: true, data: { status: "resolving-conflicts", conflicts: [...] } }`

### Requirement: Rebase 端点 conflict-resolution-in-progress guard

`POST /api/issues/:number/rebase` SHALL 检测冲突解决 agent 是否正在运行，防止重复触发。

#### Scenario: 冲突解决 agent 运行中再次请求 rebase
- **WHEN** 请求 `POST /api/issues/:number/rebase`
- **AND** 该 issue 的冲突解决 agent 正在运行
- **THEN** 返回 HTTP 409
- **AND** body 包含错误信息 "Conflict resolution in progress"

#### Scenario: 冲突解决完成后可以再次请求 rebase
- **WHEN** 请求 `POST /api/issues/:number/rebase`
- **AND** 该 issue 的冲突解决 agent 已完成（无论成功或失败）
- **THEN** 正常执行 rebase 流程
