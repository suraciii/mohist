## ADDED Requirements

### Requirement: API 提供合并队列状态查询端点

Server SHALL 提供 `GET /api/issues/merge-queue/status` 端点，返回当前合并队列的状态。

#### Scenario: 获取队列状态

- **WHEN** CLI 请求 `GET /api/issues/merge-queue/status`
- **THEN** 返回当前项目的合并队列条目列表
- **AND** 每个条目包含 `{ issueNumber, mergeState, message?, enqueuedAt }`
- **AND** 条目按入队时间排序

#### Scenario: 无项目上下文查询队列状态

- **WHEN** CLI 请求 `GET /api/issues/merge-queue/status`
- **AND** server 无当前 project 上下文
- **THEN** 返回 400 错误
- **AND** 错误信息包含 "no active project"

#### Scenario: 空队列

- **WHEN** CLI 请求 `GET /api/issues/merge-queue/status`
- **AND** 当前项目无合并队列条目
- **THEN** 返回空数组 `{ items: [] }`

### Requirement: API 提供重试合并端点

Server SHALL 提供 `POST /api/issues/:number/retry-merge` 端点，允许用户对合并失败的 issue 重新入队。

#### Scenario: 重试合并失败的 issue

- **WHEN** CLI 请求 `POST /api/issues/:number/retry-merge`
- **AND** issue 存在
- **AND** issue 的 `mergeState` 为 `build-failed` 或 `conflict`
- **THEN** issue 重新入队，`mergeState` 更新为 `pending`
- **AND** 返回 200，body 包含 `{ success: true, data: { issueNumber, mergeState: 'pending' } }`

#### Scenario: 重试非失败状态的 issue

- **WHEN** CLI 请求 `POST /api/issues/:number/retry-merge`
- **AND** issue 的 `mergeState` 不是 `build-failed` 也不是 `conflict`
- **THEN** 返回 409 Conflict
- **AND** 错误信息包含 "Issue is not in a failed merge state"

#### Scenario: 重试不存在的 issue

- **WHEN** CLI 请求 `POST /api/issues/:number/retry-merge`
- **AND** issue 不存在
- **THEN** 返回 404 错误

#### Scenario: 重试但无 worktree

- **WHEN** CLI 请求 `POST /api/issues/:number/retry-merge`
- **AND** issue 的 `mergeState` 为 `conflict` 或 `build-failed`
- **AND** 对应 worktree 已被手动删除
- **THEN** 返回 404 错误
- **AND** 错误信息包含 "No worktree found for issue #N"

## MODIFIED Requirements

### Requirement: API 提供状态查询接口

Server SHALL 提供 RESTful API 供 CLI 查询状态，基于 Hono 框架实现。Issue 详情响应 SHALL 包含 `mergeState` 字段。

#### Scenario: 获取全局状态
- **WHEN** CLI 请求 `GET /api/status`
- **THEN** 返回当前项目的 Issue 状态

#### Scenario: 获取所有项目状态
- **WHEN** CLI 请求 `GET /api/status?all=true`
- **THEN** 返回所有项目的 Issue 状态

#### Scenario: 获取单个 Issue 详情
- **WHEN** CLI 请求 `GET /api/issues/:number`
- **THEN** 返回指定 Issue 的详细信息
- **AND** 响应包含 `mergeState` 字段（值为 `pending`、`merging`、`merged`、`build-failed`、`conflict` 之一或 `null`）
