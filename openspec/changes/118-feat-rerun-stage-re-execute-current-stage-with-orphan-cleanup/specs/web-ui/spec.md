## ADDED Requirements

### Requirement: IssueDetailPage 显示 Rerun Stage 按钮

IssueDetailPage SHALL 在 issue 处于非 `done`/`draft` stage 且无 agent 运行时，显示 "Rerun Stage" 按钮。点击后 SHALL 调用 `POST /api/issues/:number/rerun`，成功后刷新 issue 状态。

#### Scenario: 显示 Rerun 按钮
- **WHEN** 用户查看 issue 详情页
- **AND** issue stage 不为 `done` 或 `draft`
- **AND** 无 agent 正在运行
- **THEN** 显示 "Rerun Stage" 按钮

#### Scenario: 点击 Rerun 按钮成功
- **WHEN** 用户点击 "Rerun Stage" 按钮
- **THEN** 调用 `POST /api/issues/:number/rerun`
- **AND** 成功后 issue 状态刷新为 `active`，agent 重新开始运行

#### Scenario: Agent 运行时隐藏 Rerun 按钮
- **WHEN** issue 有 agent 正在运行
- **THEN** 不显示 "Rerun Stage" 按钮

#### Scenario: Draft 和 Done stage 不显示 Rerun 按钮
- **WHEN** issue stage 为 `draft` 或 `done`
- **THEN** 不显示 "Rerun Stage" 按钮

### Requirement: IssueCard 显示 Rerun 快捷按钮

IssueCard SHALL 在 issue 处于非 `done`/`draft` stage 且无 agent 运行时，显示 Rerun 快捷按钮。点击后 SHALL 调用 rerun API 并刷新看板。

#### Scenario: IssueCard 显示 Rerun 按钮
- **WHEN** issue 在看板页面的卡片上
- **AND** issue stage 不为 `done` 或 `draft`
- **AND** 无 agent 正在运行
- **THEN** 卡片上显示 Rerun 快捷按钮（图标或小按钮）

#### Scenario: 点击 IssueCard Rerun 按钮
- **WHEN** 用户点击 IssueCard 上的 Rerun 按钮
- **THEN** 调用 `POST /api/issues/:number/rerun`
- **AND** 成功后看板刷新，issue 状态更新为 `active`

### Requirement: 前端 API client 提供 rerunIssue 方法

`api.ts` SHALL 新增 `rerunIssue(issueNumber)` 方法，对应后端 `POST /api/issues/:number/rerun`。

#### Scenario: rerunIssue 调用成功
- **WHEN** 调用 `api.rerunIssue(5)`
- **THEN** 发送 `POST /api/issues/5/rerun` 请求
- **AND** 返回更新后的 issue 信息

#### Scenario: rerunIssue 调用失败
- **WHEN** 调用 `api.rerunIssue(5)`
- **AND** 后端返回 409（agent 正在运行）
- **THEN** 抛出错误，前端显示错误提示
