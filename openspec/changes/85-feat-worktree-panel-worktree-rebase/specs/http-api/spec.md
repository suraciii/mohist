## ADDED Requirements

### Requirement: API 提供 worktree 状态查询端点

Server SHALL 提供 `GET /api/issues/:number/worktree-status` 端点，返回 issue worktree 的 git 状态信息。

#### Scenario: worktree 存在时返回状态

- **WHEN** 请求 `GET /api/issues/:number/worktree-status`
- **AND** issue 存在
- **AND** worktree 存在
- **THEN** 返回 200，body 为 `{ exists: true, branch, ahead, behind, canFastForward, isRebaseInProgress }`

#### Scenario: worktree 不存在时返回 exists false

- **WHEN** 请求 `GET /api/issues/:number/worktree-status`
- **AND** issue 存在
- **AND** worktree 不存在
- **THEN** 返回 200，body 为 `{ exists: false, branch: '', ahead: 0, behind: 0, canFastForward: false, isRebaseInProgress: false }`

#### Scenario: issue 不存在

- **WHEN** 请求 `GET /api/issues/:number/worktree-status`
- **AND** issue 不存在
- **THEN** 返回 404

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

#### Scenario: Rebase Issue 分支
- **WHEN** CLI 请求 `POST /api/issues/:number/rebase`
- **AND** agent 未运行
- **THEN** 系统检查前置条件（worktree 存在、stage 支持）
- **AND** 按当前 stage 执行差异化 rebase 逻辑
- **AND** 返回 `{ rebased, conflicts?, buildPassed?, message }`

#### Scenario: Rebase Issue 分支 — 排队模式
- **WHEN** CLI 请求 `POST /api/issues/:number/rebase` with `{ queue: true }` 或 query param `?queue=true`
- **AND** agent 正在运行
- **AND** stage 支持 rebase（plan/build/review）
- **THEN** 系统记录排队请求
- **AND** 返回 `{ queued: true, message: "Rebase queued for after agent completion" }`
- **AND** agent 完成后自动触发 rebase

#### Scenario: Rebase 排队 — 无 agent 运行时直接执行
- **WHEN** CLI 请求 `POST /api/issues/:number/rebase` with `queue: true`
- **AND** agent 当前未运行
- **THEN** 直接执行 rebase（等同非排队模式）
- **AND** 返回 rebase 结果

#### Scenario: Rebase 排队 — stage 不支持时拒绝
- **WHEN** CLI 请求 `POST /api/issues/:number/rebase` with `queue: true`
- **AND** issue stage 为 `backlog` 或 `explore`
- **THEN** 返回 400 错误
- **AND** 错误信息包含 "Rebase not available in current stage"

#### Scenario: Rebase — agent 运行中且未指定 queue 时拒绝
- **WHEN** CLI 请求 `POST /api/issues/:number/rebase`（无 queue 参数）
- **AND** agent 正在运行
- **THEN** 返回 409 Conflict
- **AND** 错误信息包含 "Agent is running"
