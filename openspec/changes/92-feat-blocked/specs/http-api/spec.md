## MODIFIED Requirements

### Requirement: Start handler 校验 issue status

`POST /api/issues/:number/start` SHALL 在执行前校验 issue status，blocked 的 issue 不允许 start（应使用 retry 或 restart）。

#### Scenario: Start blocked issue
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue status 为 `blocked`
- **THEN** server 返回 400 错误
- **AND** 错误信息包含 "blocked" 和提示使用 retry/restart

#### Scenario: Start active issue in draft stage
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue status 为 `active` 且 stage 为 `draft`
- **THEN** 正常启动 agent

### Requirement: Agent status API 返回可恢复 issues

`GET /api/agent/status` 返回值 SHALL 包含 `recoverableIssues` 数组和 `blockedIssues` 数组，分别列出可恢复和已 blocked 的 issue 信息。

#### Scenario: Server 重启后检测可恢复 issues
- **WHEN** server 重启
- **AND** 数据库中存在 `status = 'active'` 且 `stage` 不是 `draft` 的 issues
- **THEN** `GET /api/agent/status` 返回的 `recoverableIssues` 数组包含这些 issue 的 number 和 stage

#### Scenario: 所有 issue 正常完成时无可恢复项
- **WHEN** 所有 issue 的 status 都不是 `active`
- **THEN** `GET /api/agent/status` 返回的 `recoverableIssues` 为空数组

#### Scenario: 返回 blocked issues 列表
- **WHEN** 请求 `GET /api/agent/status`
- **AND** 数据库中存在 `status = 'blocked'` 的 issues
- **THEN** 返回的 `blockedIssues` 数组包含每个 blocked issue 的 `{ issueNumber, stage, blockedReason, retryCount }`

#### Scenario: 无 blocked issues
- **WHEN** 请求 `GET /api/agent/status`
- **AND** 没有 status 为 blocked 的 issue
- **THEN** `blockedIssues` 为空数组
