## MODIFIED Requirements

### Requirement: Web UI 实时响应 agent 暂停状态

Web UI SHALL 监听 `agent_paused` SSE 事件，在收到事件后刷新 issue 详情和列表数据，使审批提示自动出现。此外，当 `agent_paused` 事件属于非当前查看的 issue 时，SHALL 显示 info toast 通知用户。

#### Scenario: agent 暂停后审批面板自动显示
- **WHEN** agent 完成一个带 approval 的阶段
- **AND** 用户正在 Web UI 查看该 issue
- **THEN** 不需要手动刷新，审批面板自动显示 "Approve & Continue" 按钮

#### Scenario: Issue 卡片状态实时更新
- **WHEN** agent 暂停
- **AND** 用户在看板页面
- **THEN** 对应 issue 卡片自动更新状态指示器（显示 "Needs Approval" 或类似标记）

#### Scenario: agent 暂停在后台 issue 时显示 toast
- **WHEN** SSE 收到 `agent_paused` 事件，且该 issue 不是用户当前正在查看的 issue
- **THEN** 显示 info toast，消息为 "Issue #N needs approval"
