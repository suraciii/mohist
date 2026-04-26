## ADDED Requirements

### Requirement: Web UI 展示 issue 合并状态

Web UI Issue 详情页 SHALL 展示当前 issue 的合并队列状态（`mergeState`），包括状态指示和失败原因。

#### Scenario: 显示合并中状态

- **WHEN** 用户查看 issue 详情页
- **AND** issue 的 `mergeState` 为 `merging`
- **THEN** 显示合并状态指示器（如 "Merging..." 加载动画）
- **AND** 状态指示器位于 issue 详情的显著位置

#### Scenario: 显示合并成功状态

- **WHEN** 用户查看 issue 详情页
- **AND** issue 的 `mergeState` 为 `merged`
- **THEN** 显示 "Merged" 成功标记

#### Scenario: 显示合并排队状态

- **WHEN** 用户查看 issue 详情页
- **AND** issue 的 `mergeState` 为 `pending`
- **THEN** 显示 "Queued for merge" 排队标记

#### Scenario: 无合并状态时隐藏

- **WHEN** 用户查看 issue 详情页
- **AND** issue 的 `mergeState` 为 `undefined` 或 `null`
- **THEN** 不显示合并状态区域

### Requirement: Web UI 展示合并失败信息并提供重试

Web UI SHALL 在合并失败时展示失败原因和错误消息，并提供 "Retry Merge" 按钮。

#### Scenario: 显示构建失败信息

- **WHEN** 用户查看 issue 详情页
- **AND** issue 的 `mergeState` 为 `build-failed`
- **THEN** 显示错误面板，包含失败原因 "Build failed after merge"
- **AND** 显示错误详情（构建输出的关键错误信息）
- **AND** 显示 "Retry Merge" 按钮

#### Scenario: 显示冲突失败信息

- **WHEN** 用户查看 issue 详情页
- **AND** issue 的 `mergeState` 为 `conflict`
- **THEN** 显示错误面板，包含失败原因 "Merge conflict"
- **AND** 显示冲突消息
- **AND** 显示 "Retry Merge" 按钮

#### Scenario: 用户点击重试

- **WHEN** 用户点击 "Retry Merge" 按钮
- **THEN** 调用 `POST /api/issues/:number/retry-merge`
- **AND** 成功后 issue 的合并状态更新为 `pending`
- **AND** 错误面板消失，显示排队状态

#### Scenario: 重试失败

- **WHEN** 用户点击 "Retry Merge" 按钮
- **AND** API 返回错误（如 409 非失败状态）
- **THEN** 显示错误提示
- **AND** 错误面板保持显示

### Requirement: Web UI 实时响应合并队列 SSE 事件

Web UI SHALL 监听合并队列相关的 SSE 事件，实时更新 issue 的合并状态。

#### Scenario: 收到 merge_queued 事件后更新

- **WHEN** SSE 收到 `merge_queued` 事件
- **AND** 用户正在查看对应 issue 详情页
- **THEN** 合并状态自动更新为 "Queued for merge"

#### Scenario: 收到 merge_started 事件后更新

- **WHEN** SSE 收到 `merge_started` 事件
- **AND** 用户正在查看对应 issue 详情页
- **THEN** 合并状态自动更新为 "Merging..."

#### Scenario: 收到 merge_completed 事件后更新

- **WHEN** SSE 收到 `merge_completed` 事件
- **AND** 用户正在查看对应 issue 详情页
- **THEN** 合并状态自动更新为 "Merged" 成功标记

#### Scenario: 收到 merge_failed 事件后更新

- **WHEN** SSE 收到 `merge_failed` 事件
- **AND** 用户正在查看对应 issue 详情页
- **THEN** 自动显示错误面板和重试按钮
- **AND** 不需要手动刷新页面
