## MODIFIED Requirements

### Requirement: Web UI 实时响应 agent 暂停状态

Web UI SHALL 监听 `agent_paused` SSE 事件，在收到事件后刷新 issue 详情和列表数据，使审批提示或检查结果面板自动出现。

#### Scenario: agent 暂停后 Check Results Panel 自动显示
- **WHEN** agent 完成 Check stage 检查套件并暂停等待审批
- **AND** 用户正在 Web UI 查看该 issue
- **THEN** 不需要手动刷新，Check Results Panel 自动显示检查结果和相应操作按钮

#### Scenario: Issue 卡片状态实时更新
- **WHEN** agent 暂停在 Check stage
- **AND** 用户在看板页面
- **THEN** 对应 issue 卡片自动更新状态指示器
- **AND** 看板列标题显示 "Check"（而非 "Review"）

### Requirement: 移除无功能的 Skip 按钮

Issue 详情页的审批面板 SHALL 只保留可用操作。当前 Skip 按钮无后端支持，SHALL 被移除以避免误导用户。

#### Scenario: 审批面板只显示可用操作
- **WHEN** 用户查看需要审批的 issue
- **THEN** 审批面板只显示 Check Results Panel 中基于检查结果确定的操作按钮
- **AND** 不显示无功能的 Skip 按钮
