## MODIFIED Requirements

### Requirement: Web UI 实时响应 agent 暂停状态

Web UI SHALL 监听 `agent_paused` SSE 事件，在收到事件后刷新 issue 详情和列表数据，使审批提示自动出现。审批面板 SHALL 从 `approvalState.output` 展示审查报告，不依赖 comments 来决定 Approve 按钮的显示。

#### Scenario: agent 暂停后审批面板自动显示
- **WHEN** agent 完成一个带 approval 的阶段
- **AND** 用户正在 Web UI 查看该 issue
- **THEN** 不需要手动刷新，审批面板自动显示 "Approve & Continue" 按钮
- **AND** Approve 按钮的显示不依赖 issue 是否有 comments

#### Scenario: Issue 卡片状态实时更新
- **WHEN** agent 暂停
- **AND** 用户在看板页面
- **THEN** 对应 issue 卡片自动更新状态指示器（显示 "Needs Approval" 或类似标记）

#### Scenario: 审批面板展示审查报告而非 comments
- **WHEN** issue 处于审批状态
- **THEN** 审批面板从 `approvalState.output` 读取并展示审查报告
- **AND** 当 `approvalState.output` 为空时 fallback 到最新 agent comment 的 body

### Requirement: 移除无功能的 Skip 按钮

Issue 详情页的审批面板 SHALL 只保留可用操作。当前 Skip 按钮无后端支持，SHALL 被移除以避免误导用户。

#### Scenario: 审批面板只显示可用操作
- **WHEN** 用户查看需要审批的 issue
- **THEN** 审批面板只显示 "Approve & Continue" 按钮
- **AND** 不显示无功能的 Skip 按钮
