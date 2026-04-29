## ADDED Requirements

### Requirement: Blocked 状态展示原因说明和操作按钮

Web UI IssueDetailPage 在 issue status 为 blocked 时 SHALL 展示 blockedReason 人话描述，并提供 Retry 和 Restart 操作按钮，替换现有单一的 Reopen 按钮。

#### Scenario: Blocked 状态展示 reason 面板
- **WHEN** 用户查看一个 status 为 blocked 的 issue
- **THEN** IssueDetailPage 显示 blocked reason 面板（红色/橙色警告样式）
- **AND** 面板包含人话原因描述（来自 issue.blockedReason）
- **AND** 面板包含进度提示（如有可恢复进度）
- **AND** 面板包含 "重试" 和 "重新开始" 操作按钮

#### Scenario: blockedReason 为空时显示默认提示
- **WHEN** issue status 为 blocked 但 blockedReason 为 null
- **THEN** 面板显示默认提示 "Issue 已暂停。可以重试或重新开始。"

#### Scenario: 原有 Reopen 按钮保留但降级
- **WHEN** issue status 为 blocked
- **THEN** 原有的 "Reopen" 按钮仍可用（作为 "重新开始" 的别名）
- **AND** 主要操作入口为 "重试" 和 "重新开始" 按钮

### Requirement: 前端 API client 支持新端点

前端 API client SHALL 新增 `retryIssue` 和 `restartIssue` 方法，并扩展 Issue 类型。

#### Scenario: Issue 类型扩展
- **WHEN** 检查前端 Issue 类型定义
- **THEN** 包含 `blockedReason?: string` 字段
- **AND** 包含 `retryCount?: number` 字段

#### Scenario: retryIssue 方法
- **WHEN** 调用 `api.retryIssue(number)`
- **THEN** 发送 `POST /api/issues/:number/retry`
- **AND** 返回更新后的 issue 数据

#### Scenario: restartIssue 方法
- **WHEN** 调用 `api.restartIssue(number)`
- **THEN** 发送 `POST /api/issues/:number/restart`
- **AND** 返回更新后的 issue 数据
