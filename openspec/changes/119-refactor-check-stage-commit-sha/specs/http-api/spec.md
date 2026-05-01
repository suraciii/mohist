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

### Requirement: Approve 端点 SHA 校验

`POST /api/issues/:number/approve` SHALL 在 approval stage 为 Check 时，校验 worktree HEAD SHA 是否匹配 CheckSuite.snapshotSha。不匹配时 SHALL 自动触发重跑检查。

#### Scenario: SHA 匹配时批准生效
- **WHEN** 用户 POST `POST /api/issues/:number/approve`
- **AND** approval stage 为 Check
- **AND** worktree HEAD SHA == CheckSuite.snapshotSha
- **THEN** 批准生效，issue 进入 merge queue
- **AND** 返回 200

#### Scenario: SHA 不匹配时自动重跑检查
- **WHEN** 用户 POST `POST /api/issues/:number/approve`
- **AND** approval stage 为 Check
- **AND** worktree HEAD SHA != CheckSuite.snapshotSha
- **THEN** 返回 202 和提示信息 "Code has changed since last check, re-running checks"
- **AND** 自动重跑 Check stage（更新 snapshotSha，从头执行检查循环）
- **AND** 不进入 merge queue

#### Scenario: 无活跃 CheckSuite 时正常批准
- **WHEN** 用户 POST `POST /api/issues/:number/approve`
- **AND** approval stage 为 Check
- **AND** 无活跃 CheckSuite（首次或 recovery 场景）
- **THEN** 批准生效，issue 进入 merge queue
- **AND** 返回 200

### Requirement: Issue 详情包含 CheckSuite 信息

`GET /api/issues/:number` 返回值 SHALL 包含当前活跃的 CheckSuite 信息（如果存在）。

#### Scenario: Issue 有活跃 CheckSuite
- **WHEN** CLI 请求 `GET /api/issues/:number`
- **AND** 该 issue 有活跃 CheckSuite（status 为 running 或 awaiting-approval）
- **THEN** 返回的 issue 对象包含 `checkSuite` 字段
- **AND** 包含完整的 checks 状态和 snapshotSha

#### Scenario: Issue 无活跃 CheckSuite
- **WHEN** CLI 请求 `GET /api/issues/:number`
- **AND** 该 issue 无活跃 CheckSuite
- **THEN** 返回的 issue 对象中 `checkSuite` 字段为 null
