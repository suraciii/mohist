## ADDED Requirements

### Requirement: Draft issue 详情页 Explore 按钮

IssueDetailPage 在 issue stage 为 `draft` 时，SHALL 在 Actions 区域显示 "Explore" 按钮。

#### Scenario: Draft issue 显示 Explore 按钮
- **WHEN** 用户查看 stage 为 `draft` 的 issue 详情页
- **THEN** Actions 区域显示 "Explore" 按钮（与 Start 按钮并列）

#### Scenario: 非 Draft issue 不显示 Explore 按钮
- **WHEN** 用户查看 stage 不是 `draft` 的 issue 详情页
- **THEN** Actions 区域不显示 "Explore" 按钮

### Requirement: Explore 按钮跳转逻辑

点击 Explore 按钮时，SHALL 查找该 issue 关联的 session：有则跳转，无则创建并跳转。同一 issue 只关联一个 session。

#### Scenario: issue 已有关联 session
- **WHEN** 用户点击 issue 的 "Explore" 按钮
- **AND** 该 issueId 已关联一个 explore session
- **THEN** 直接导航到 `/explore/:sessionId`

#### Scenario: issue 无关联 session 则创建
- **WHEN** 用户点击 issue 的 "Explore" 按钮
- **AND** 该 issueId 未关联任何 explore session
- **THEN** 调用 `POST /api/explore` with `{ projectId, issueId }` 创建新 session
- **AND** 创建成功后导航到 `/explore/:sessionId`

#### Scenario: 创建失败显示错误
- **WHEN** 用户点击 "Explore" 按钮
- **AND** 创建 session 请求失败
- **THEN** 在 Actions 区域显示错误提示
