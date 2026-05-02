## MODIFIED Requirements

### Requirement: Web UI 实时响应 agent 暂停状态

Web UI SHALL 监听 `agent_paused` SSE 事件，在收到事件后刷新 issue 详情和列表数据，使审批提示自动出现。IssueDetailPage 的 Changes 面板 SHALL 在所有阶段可见（无 DIFF_STAGES 限制），位于 Description 之后、TaskList 之前，并显示摘要统计信息。

#### Scenario: agent 暂停后审批面板自动显示
- **WHEN** agent 完成一个带 approval 的阶段
- **AND** 用户正在 Web UI 查看该 issue
- **THEN** 不需要手动刷新，审批面板自动显示 "Approve & Continue" 按钮
- **AND** Changes 面板在所有阶段均可见，展示当前变更摘要

#### Scenario: Issue 卡片状态实时更新
- **WHEN** agent 暂停
- **AND** 用户在看板页面
- **THEN** 对应 issue 卡片自动更新状态指示器（显示 "Needs Approval" 或类似标记）

#### Scenario: Changes 面板无阶段限制
- **WHEN** 用户查看任意阶段的 issue 详情页
- **THEN** Changes 面板始终可见（包括 Backlog 阶段）
- **AND** 无 DIFF_STAGES 常量控制其显示

#### Scenario: Changes 面板位置在 Description 之后
- **WHEN** 用户查看 issue 详情页
- **THEN** Changes 面板出现在 Description 之后、TaskList 之前
- **AND** Comments 区域下方不再有重复的 Changes 面板
