## MODIFIED Requirements

### Requirement: Web UI 实时响应 agent 暂停状态

Web UI SHALL 监听 `agent_paused` SSE 事件，在收到事件后刷新 issue 详情和列表数据，使审批提示自动出现。审批面板 SHALL 根据当前阶段（Plan/Review）展示对应的差异化审查内容和动作按钮。

#### Scenario: agent 暂停后审批面板自动显示

- **WHEN** agent 完成一个带 approval 的阶段
- **AND** 用户正在 Web UI 查看该 issue
- **THEN** 不需要手动刷新，审批面板自动显示
- **AND** 面板内容根据 `issue.stage` 和 `approvalState.output` 展示对应的审查内容和动作按钮

#### Scenario: Issue 卡片状态实时更新

- **WHEN** agent 暂停
- **AND** 用户在看板页面
- **THEN** 对应 issue 卡片自动更新状态指示器（显示 "Needs Approval" 或类似标记）

### Requirement: 移除无功能的 Skip 按钮

Issue 详情页的审批面板 SHALL 只保留阶段和 verdict 对应的可用操作。当前 Skip 按钮无后端支持，SHALL 被移除以避免误导用户。单一的 "Approve & Continue" SHALL 被阶段差异化的动作按钮替代。

#### Scenario: 审批面板只显示阶段对应的可用操作

- **WHEN** 用户查看需要审批的 issue
- **THEN** 审批面板显示 `stage-differentiated-approval` spec 中定义的动作按钮
- **AND** 不显示无功能的 Skip 按钮
- **AND** 不显示通用的 "Approve & Continue" 按钮（由阶段差异化按钮替代）
