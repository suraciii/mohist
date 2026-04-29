## MODIFIED Requirements

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

## ADDED Requirements

### Requirement: IssueModelSelector 显示当前默认模型名称

IssueModelSelector 的 "Use default" 选项 SHALL 显示当前默认 coder model 名称，格式为 "Use default (model-name)"。

#### Scenario: 有默认模型配置时显示模型名

- **WHEN** `GET /api/opencode-config/model` 返回 `{ model: "anthropic/claude-sonnet-4" }`
- **AND** IssueModelSelector 中当前选中了某个 per-issue model override
- **THEN** 清除按钮显示 "Use default (claude-sonnet-4)"

#### Scenario: 无默认模型配置时

- **WHEN** `GET /api/opencode-config/model` 返回 `{ model: null }`
- **AND** IssueModelSelector 中当前选中了某个 per-issue model override
- **THEN** 清除按钮显示 "Use default"

#### Scenario: 按钮显示文本包含默认模型名

- **WHEN** `GET /api/opencode-config/model` 返回 `{ model: "anthropic/claude-sonnet-4" }`
- **AND** IssueModelSelector 无 per-issue override（使用默认）
- **THEN** 触发按钮显示 "claude-sonnet-4"（取模型 ID 最后一段）

#### Scenario: 默认模型加载中

- **WHEN** 默认模型 API 正在加载
- **THEN** 按钮显示 "Use default"（不阻塞 UI）
