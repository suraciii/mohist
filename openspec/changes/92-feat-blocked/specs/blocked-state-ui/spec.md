## ADDED Requirements

### Requirement: Blocked 状态展示原因说明

IssueDetailPage 在 issue status 为 blocked 时 SHALL 展示 blockedReason 人话描述，替代或补充现有 badge。

#### Scenario: 展示 blocked reason

- **WHEN** 用户查看一个 status 为 blocked 的 issue
- **THEN** IssueDetailPage 显示 blocked reason 面板
- **AND** 面板包含人话原因描述（来自 issue.blockedReason）
- **AND** 面板视觉上突出（红色/橙色警告样式）

#### Scenario: blocked reason 为空时显示默认提示

- **WHEN** 用户查看一个 status 为 blocked 的 issue
- **AND** issue.blockedReason 为 null 或空
- **THEN** 面板显示默认提示 "Issue 已暂停。可以重试或重新开始。"

### Requirement: Blocked 状态展示进度保留提示

当 blocked issue 有可恢复的 task 进度时，IssueDetailPage SHALL 展示进度信息。

#### Scenario: 部分任务完成时展示进度

- **WHEN** 用户查看一个 status 为 blocked 的 issue
- **AND** issue 在 build stage，tasks.json 中有部分 task 已完成
- **THEN** 面板显示进度提示，如 "已完成 3/8 个任务，可从断点恢复"

#### Scenario: 无 task 进度时不显示进度提示

- **WHEN** 用户查看一个 status 为 blocked 的 issue
- **AND** issue 不在 build stage 或无 tasks.json
- **THEN** 不显示进度提示

### Requirement: Blocked 状态展示操作按钮

blocked 状态下 IssueDetailPage SHALL 展示操作按钮，替换现有的单一 "Reopen" 按钮。

#### Scenario: 显示重试和重新开始按钮

- **WHEN** 用户查看一个 status 为 blocked 的 issue
- **THEN** 面板显示以下按钮：
  - "重试" (Retry) — 调用 `POST /api/issues/:number/retry`
  - "重新开始" (Restart) — 调用 `POST /api/issues/:number/restart`，点击后弹出确认对话框

#### Scenario: 重试按钮反馈

- **WHEN** 用户点击 "重试" 按钮
- **THEN** 按钮显示 loading 状态
- **AND** API 返回成功后 issue 状态自动更新为 active
- **AND** 面板消失，显示运行中状态

#### Scenario: 重新开始需确认

- **WHEN** 用户点击 "重新开始" 按钮
- **THEN** 弹出确认对话框，提示 "这将丢弃所有进度并从头开始。确定？"
- **AND** 用户确认后才调用 restart API

#### Scenario: API 错误反馈

- **WHEN** retry 或 restart API 返回错误
- **THEN** 按钮恢复可点击状态
- **AND** 面板下方显示错误信息

### Requirement: Blocked 事件实时更新

IssueDetailPage SHALL 监听 `agent_blocked` SSE 事件，blocked 状态变化时自动刷新面板。

#### Scenario: 实时展示 blocked reason

- **WHEN** 用户正在查看 issue 详情页
- **AND** agent 在后台进入 blocked 状态
- **THEN** SSE 收到 `agent_blocked` 事件后自动刷新 issue 数据
- **AND** blocked reason 面板自动出现

#### Scenario: 重试后 blocked 面板自动消失

- **WHEN** 用户点击重试按钮后 agent 恢复运行
- **THEN** SSE 收到状态变更事件后 blocked 面板自动消失
- **AND** 恢复正常运行状态显示

### Requirement: 前端 API client 支持 retry 和 restart

前端 API client SHALL 新增 `retryIssue` 和 `restartIssue` 方法。

#### Scenario: retryIssue 调用

- **WHEN** 调用 `api.retryIssue(number)`
- **THEN** 发送 `POST /api/issues/:number/retry`
- **AND** 返回更新后的 issue 数据

#### Scenario: restartIssue 调用

- **WHEN** 调用 `api.restartIssue(number)`
- **THEN** 发送 `POST /api/issues/:number/restart`
- **AND** 返回更新后的 issue 数据

### Requirement: IssueCard 显示 blocked reason 摘要

看板页面的 IssueCard 在 issue status 为 blocked 时 SHALL 显示 blockedReason 的单行摘要。

#### Scenario: IssueCard 显示 blocked reason

- **WHEN** 看板页面渲染一个 blocked issue 的卡片
- **THEN** 卡片底部显示 blockedReason 的截断摘要（单行，超过 60 字符截断加省略号）
- **AND** 摘要使用红色/橙色文本
