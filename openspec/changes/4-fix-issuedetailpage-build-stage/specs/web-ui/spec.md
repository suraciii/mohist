## MODIFIED Requirements

### Requirement: Web UI 实时响应 agent 暂停状态

Web UI SHALL 监听 `agent_paused` SSE 事件，在收到事件后刷新 issue 详情和列表数据，使审批提示自动出现。审批按钮的显示 SHALL 基于 `issue.approvalState?.status === "awaiting"` 判断，而非硬编码特定 stage。IssueDetailPage 和 IssueCard 均不得使用 `APPROVAL_STAGES` 或任何 stage 白名单来决定审批 UI 的显示。

#### Scenario: agent 暂停后审批面板自动显示
- **WHEN** agent 完成一个带 approval 的阶段（plan 或 build 等）
- **AND** issue 的 `approvalState.status` 变为 `"awaiting"`
- **AND** 用户正在 Web UI 查看该 issue
- **THEN** 不需要手动刷新，审批面板自动显示 "Approve & Continue" 按钮

#### Scenario: plan 阶段等审批时显示 approve 按钮
- **WHEN** issue 处于 plan 阶段
- **AND** `issue.approvalState.status === "awaiting"`
- **AND** issue 状态为 Active
- **AND** 没有 agent 正在运行
- **THEN** IssueDetailPage 显示 "Approve & Continue" 按钮

#### Scenario: Issue 卡片状态实时更新
- **WHEN** issue 的 `approvalState.status` 变为 `"awaiting"`
- **AND** 用户在看板页面
- **THEN** 对应 IssueCard 显示 "Needs Approval" 状态指示器

#### Scenario: 非 awaiting 状态不显示审批按钮
- **WHEN** issue 的 `approvalState.status` 不是 `"awaiting"`
- **THEN** IssueDetailPage 不显示审批按钮
- **AND** IssueCard 不显示 "Needs Approval" 标记
