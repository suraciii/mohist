## ADDED Requirements

### Requirement: 前端 Reopen 按钮对 Closed issue 可见

前端 Actions 面板和 IssueCard SHALL 对 Closed 状态的 issue 显示 Reopen 按钮，与后端 reopen API 的适用范围（Closed、Blocked、Paused、Interrupted）对齐。

#### Scenario: Closed issue 详情页显示 Reopen 按钮
- **WHEN** issue.status === `IssueStatus.Closed`
- **THEN** IssueDetailPage Actions 面板显示 "Reopen" 按钮
- **AND** 点击后调用 `api.reopenIssue(issueNumber)`

#### Scenario: Reopen 按钮调用正确的 API
- **WHEN** 用户点击 Reopen 按钮
- **THEN** 调用 `POST /api/issues/:number/reopen`
- **AND** 成功后刷新 issue 数据和 agent status
