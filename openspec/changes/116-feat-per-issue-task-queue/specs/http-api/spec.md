## MODIFIED Requirements

### Requirement: API 提供操作接口

Server SHALL 提供 RESTful API 供 CLI 执行操作，基于 Hono 框架实现。API handler SHALL 通过 IssueService 操作 issue 数据，不直接调用 StateManager 的 CRUD 方法。Mutation endpoints（`/start`、`/approve`、`/reopen`、`/rebase`、`/propose`）SHALL 使用 `enqueue()` 将操作入队并返回 202 Accepted。

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

#### Scenario: 启动 Issue 处理（enqueue）

- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **THEN** handler SHALL call `agentRunner.enqueue(issueId, 'start-pipeline', payload)`
- **AND** 返回 202 Accepted with `{ taskId, status: 'pending' | 'running', queuePosition? }`

#### Scenario: Approve Issue（enqueue）

- **WHEN** CLI 请求 `POST /api/issues/:number/approve`
- **THEN** handler SHALL call `agentRunner.enqueue(issueId, 'resume-pipeline', payload)`
- **AND** 返回 202 Accepted with `{ taskId, status, queuePosition? }`

#### Scenario: Reopen Issue（enqueue）

- **WHEN** CLI 请求 `POST /api/issues/:number/reopen`
- **THEN** handler SHALL call `agentRunner.enqueue(issueId, 'resume-pipeline', payload)`
- **AND** 返回 202 Accepted with `{ taskId, status, queuePosition? }`

#### Scenario: Rebase Issue（enqueue）

- **WHEN** CLI 请求 `POST /api/issues/:number/rebase`
- **THEN** handler SHALL call `agentRunner.enqueue(issueId, 'rebase', payload)`
- **AND** 返回 202 Accepted with `{ taskId, status, queuePosition? }`

#### Scenario: Propose Issue（enqueue）

- **WHEN** CLI 请求 `POST /api/issues/:number/propose`
- **THEN** handler SHALL call `agentRunner.enqueue(issueId, 'start-pipeline', payload)`
- **AND** 返回 202 Accepted with `{ taskId, status, queuePosition? }`

### Requirement: Start handler 校验 issue status

`POST /api/issues/:number/start` SHALL 在 enqueue 前校验 issue status，blocked 的 issue 不允许 start。

#### Scenario: Start blocked issue

- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue status 为 `blocked`
- **THEN** server 返回 400 错误
- **AND** 错误信息包含 "blocked"

#### Scenario: Start active issue in draft stage

- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue status 为 `active` 且 stage 为 `draft`
- **THEN** handler SHALL enqueue a `start-pipeline` task
- **AND** 返回 202 Accepted

## ADDED Requirements

### Requirement: Issue task queue query API

Server SHALL provide an endpoint to query the task queue status for a specific issue.

#### Scenario: Get queue status for an issue

- **WHEN** CLI 请求 `GET /api/issues/:number/queue`
- **THEN** 返回 200 with `{ running: TaskInfo | null, pending: TaskInfo[], queueLength: number }`
- **AND** `TaskInfo` 包含 `{ taskId, taskType, priority, status, enqueuedAt, startedAt? }`

#### Scenario: Get queue for issue with no tasks

- **WHEN** CLI 请求 `GET /api/issues/:number/queue` 且该 issue 无任务
- **THEN** 返回 200 with `{ running: null, pending: [], queueLength: 0 }`

### Requirement: Issue task cancellation API

Server SHALL provide an endpoint to cancel a specific pending task.

#### Scenario: Cancel a pending task

- **WHEN** CLI 请求 `DELETE /api/issues/:number/queue/:taskId` 且 task status 为 `pending`
- **THEN** 该 task SHALL be cancelled
- **AND** 返回 200 with `{ cancelled: true }`

#### Scenario: Cancel a running task

- **WHEN** CLI 请求 `DELETE /api/issues/:number/queue/:taskId` 且 task status 为 `running`
- **THEN** 返回 409 Conflict
- **AND** 错误信息 "Cannot cancel a running task"

#### Scenario: Cancel non-existent task

- **WHEN** CLI 请求 `DELETE /api/issues/:number/queue/:taskId` 且 taskId 不存在
- **THEN** 返回 404

### Requirement: Force-stop uses cancelAll

`POST /api/issues/:number/force-stop` SHALL call `agentRunner.cancelAll(issueId)` to cancel all pending tasks and force-stop the running task.

#### Scenario: Force-stop an issue

- **WHEN** CLI 请求 `POST /api/issues/:number/force-stop`
- **THEN** handler SHALL call `agentRunner.cancelAll(issueId)`
- **AND** all pending tasks for the issue SHALL be cancelled
- **AND** the running task (if any) SHALL be force-stopped
- **AND** 返回 200

## REMOVED Requirements

### Requirement: Removed endpoints return 404
**Reason**: `/approve` endpoint is restored as an enqueue-based endpoint. `/resume` and `/pause` remain removed.
**Migration**: Use `POST /api/issues/:number/approve` which now enqueues a `resume-pipeline` task and returns 202.
