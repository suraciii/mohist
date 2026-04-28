## MODIFIED Requirements

### Requirement: Issue 卡片状态实时更新

Web UI SHALL 监听 `agent_paused` SSE 事件，在收到事件后刷新 issue 详情和列表数据，使审批提示自动出现。看板页面上的 IssueCard SHALL 通过条件 badge 展示当前状态。

#### Scenario: agent 暂停后审批面板自动显示

- **WHEN** agent 完成一个带 approval 的阶段
- **AND** 用户正在 Web UI 查看该 issue
- **THEN** 不需要手动刷新，审批面板自动显示 "Approve & Continue" 按钮

#### Scenario: Issue 卡片状态实时更新

- **WHEN** agent 暂停
- **AND** 用户在看板页面
- **THEN** 对应 issue 卡片右上角显示 amber "Approval" badge
- **AND** 卡片状态自动更新，无需手动刷新

#### Scenario: Agent 运行中卡片实时更新

- **WHEN** issue 的 agent 开始运行
- **AND** 用户在看板页面
- **THEN** 对应 issue 卡片右上角显示蓝色脉冲 "Running" badge
- **AND** agent 结束后 badge 自动消失
