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

### Requirement: Web UI 展示 agent 提问

Web UI Issue 详情页 SHALL 展示当前 issue 的 pending 问题，并提供回复界面。

#### Scenario: 收到问题通知后显示问题面板
- **WHEN** SSE 收到 `question_asked` 事件（当前 issue）
- **THEN** Issue 详情页显示问题面板，包含问题文本和回复输入框

#### Scenario: 用户回复问题
- **WHEN** 用户在问题面板输入回复并点击提交
- **THEN** 调用 `POST /api/questions/:id/reply` 发送回复
- **AND** 问题面板更新为已回复状态
- **AND** SSE 收到 `question_answered` 事件后刷新 issue 状态

#### Scenario: 无 pending 问题时隐藏面板
- **WHEN** 当前 issue 没有 pending 状态的问题
- **THEN** 不显示问题面板

### Requirement: Issue Detail Page 提供 ModelSelector

Web UI Issue 详情页 SHALL 在 Actions 区域提供 ModelSelector 组件，允许用户为当前 issue 设置或清除 per-issue model override。ModelSelector SHALL 复用 explore 页面已有的 `ModelSelector` 组件。

#### Scenario: 设置 per-issue model
- **WHEN** 用户在 Issue 详情页的 ModelSelector 中选择 `"openai/gpt-4o"`
- **THEN** 前端调用 `PATCH /api/issues/:number` with `{ model: "openai/gpt-4o" }`
- **AND** 成功后 ModelSelector 显示当前选中的模型

#### Scenario: 清除 per-issue model
- **WHEN** 用户在 Issue 详情页的 ModelSelector 中选择 "Use default" 或清除选项
- **THEN** 前端调用 `PATCH /api/issues/:number` with `{ model: null }`
- **AND** 成功后 ModelSelector 显示为默认状态

#### Scenario: 显示当前 model override
- **WHEN** 用户打开 Issue 详情页
- **AND** issue 有 `model` 设置（非 null）
- **THEN** ModelSelector 显示当前覆盖的模型

#### Scenario: 无 model override 时显示默认状态
- **WHEN** 用户打开 Issue 详情页
- **AND** issue 的 `model` 为 null 或 undefined
- **THEN** ModelSelector 显示为未设置状态（暗示使用 stageModels/global 默认）
