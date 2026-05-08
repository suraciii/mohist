## ADDED Requirements

### Requirement: Web UI 支持删除 Issue 评论

Web UI Issue detail comments section SHALL allow users to delete individual comments without deleting the issue.

#### Scenario: 显示评论删除入口
- **WHEN** 用户打开 Issue detail 页面
- **AND** issue 有 comments
- **THEN** 每条 comment 旁显示 Delete 操作

#### Scenario: 删除前确认
- **WHEN** 用户点击某条 comment 的 Delete 操作
- **THEN** Web UI 在发送删除请求前要求用户确认

#### Scenario: 删除中状态
- **WHEN** comment 删除请求正在进行
- **THEN** 对应删除操作显示 pending 或 disabled 状态

#### Scenario: 删除失败展示错误
- **WHEN** comment 删除请求失败
- **THEN** Web UI 显示可理解的错误信息
- **AND** comment 仍显示在当前 issue detail 中

#### Scenario: 删除成功后刷新评论列表
- **WHEN** comment 删除请求成功
- **THEN** Web UI 刷新 issue detail 或从本地列表移除该 comment
- **AND** 被删除的 comment 不再显示
- **AND** issue 本体仍显示在详情页
