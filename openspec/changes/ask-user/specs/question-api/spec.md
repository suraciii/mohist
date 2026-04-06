## MODIFIED Requirements

### Requirement: Web UI 展示 agent 提问

Web UI Issue 详情页 SHALL 展示当前 issue 的 pending 问题，并提供回复界面。

#### Scenario: 收到问题通知后显示问题面板
- **WHEN** SSE 收到 `question_asked` 事件（当前 issue）
- **THEN** Issue 详情页显示问题面板，包含问题文本和回复输入框

#### Scenario: 用户回复问题
- **WHEN** 用户在问题面板输入回复并点击提交
- **THEN** 调用 `POST /api/questions/:id/reply` 发送回复
- **AND** 问题面板更新为已回复状态
- **AND** SSE 收到 `question_answered` 事件后刷新 issue 状态

#### Scenario: 无 pending 问题时隐藏面板
- **WHEN** 当前 issue 没有 pending 状态的问题
- **THEN** 不显示问题面板
