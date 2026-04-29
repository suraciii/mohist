## MODIFIED Requirements

### Requirement: Web UI 实时响应 agent 暂停状态

Web UI SHALL 监听 `agent_paused` SSE 事件，在收到事件后刷新 issue 详情和列表数据，使审批提示自动出现。

#### Scenario: agent 暂停后审批面板自动显示
- **WHEN** agent 完成一个带 approval 的阶段
- **AND** 用户正在 Web UI 查看该 issue
- **THEN** 不需要手动刷新，审批面板自动显示 "Approve & Continue" 按钮

#### Scenario: Issue 卡片状态实时更新
- **WHEN** agent 暂停
- **AND** 用户在看板页面
- **THEN** 对应 issue 卡片自动更新状态指示器（显示 "Needs Approval" 或类似标记）

### Requirement: Web UI 展示 rebase 冲突解决进度

Web UI SHALL 监听 `rebase_conflict` SSE 事件的 `status` 字段，在冲突自动解决过程中展示进度状态。

#### Scenario: 冲突检测中显示 resolving 状态
- **WHEN** SSE 收到 `rebase_conflict` 事件且 `status` 为 `resolving`
- **THEN** rebase 按钮变为禁用状态
- **AND** 显示 "Resolving conflicts..." 加载提示，附带冲突文件列表
- **AND** 不显示红色错误提示

#### Scenario: 冲突解决完成后恢复
- **WHEN** SSE 收到 `rebase_completed` 事件
- **AND** 之前有 resolving 状态
- **THEN** 隐藏 resolving 提示
- **AND** 显示 rebase 成功状态（含 `autoResolved: true` 标识时显示 "Auto-resolved"）

#### Scenario: 冲突解决失败时显示错误
- **WHEN** SSE 收到 `rebase_conflict` 事件且 `status` 为 `failed`
- **THEN** 显示红色错误提示 "Agent failed to resolve conflicts"
- **AND** 显示冲突文件列表
- **AND** rebase 按钮恢复可用状态，允许用户重试
