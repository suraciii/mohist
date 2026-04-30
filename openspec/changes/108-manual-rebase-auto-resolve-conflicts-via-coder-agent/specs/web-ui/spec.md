## ADDED Requirements

### Requirement: Web UI 展示 rebase 冲突解决进度

Web UI IssueDetailPage SHALL 处理 rebase 端点的 202 响应，并通过 SSE 事件实时展示冲突解决进度。

#### Scenario: 收到 202 后展示 resolving 状态
- **WHEN** 用户点击 Rebase 按钮
- **AND** 后端返回 202 `{ status: "resolving-conflicts" }`
- **THEN** UI 展示 "Resolving conflicts..." 进度状态
- **AND** Rebase 按钮变为禁用状态

#### Scenario: SSE 收到 agent_conflict_resolution_started
- **WHEN** SSE 推送 `agent_conflict_resolution_started` 事件（当前 issue）
- **THEN** UI 展示冲突解决 agent 工作状态

#### Scenario: SSE 收到 agent_conflict_resolution_completed
- **WHEN** SSE 推送 `agent_conflict_resolution_completed` 事件（当前 issue）
- **AND** 随后收到 `rebase_completed` 事件
- **THEN** UI 更新为 "Rebase completed" 状态
- **AND** 恢复正常操作状态

#### Scenario: SSE 收到 agent_conflict_resolution_failed
- **WHEN** SSE 推送 `agent_conflict_resolution_failed` 事件（当前 issue）
- **THEN** UI 展示冲突解决失败信息
- **AND** Rebase 按钮恢复可点击状态

#### Scenario: SSE 收到 rebase_conflict 含 error
- **WHEN** SSE 推送 `rebase_conflict` 事件且 payload 包含 `error` 字段
- **THEN** UI 展示错误信息
- **AND** Rebase 按钮恢复可点击状态
