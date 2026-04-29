## ADDED Requirements

### Requirement: Settings > General 展示 Default Coder Model 配置

GeneralSettingsSection SHALL 展示 "Default Coder Model" 字段，允许用户查看和修改 coder agent 的默认模型。

#### Scenario: 有配置时显示当前模型

- **WHEN** `GET /api/opencode-config/model` 返回 `{ model: "anthropic/claude-sonnet-4" }`
- **THEN** "Default Coder Model" 字段显示 "claude-sonnet-4"（取 `/` 后最后一段作为显示名）

#### Scenario: 无配置时显示 placeholder

- **WHEN** `GET /api/opencode-config/model` 返回 `{ model: null }`
- **THEN** "Default Coder Model" 字段显示 placeholder 文本 "opencode default"

#### Scenario: 加载中显示骨架屏

- **WHEN** 配置和模型列表正在加载
- **THEN** "Default Coder Model" 区域显示 skeleton loading 状态

### Requirement: Default Coder Model 使用模型选择器下拉

"Default Coder Model" 字段 SHALL 使用与 IssueModelSelector 相同的下拉搜索 UI 模式，模型列表来源为 `GET /opencode/models`（opencode 实际可用的模型）。

#### Scenario: 点击打开下拉列表

- **WHEN** 用户点击 "Default Coder Model" 按钮
- **THEN** 显示可搜索的模型下拉列表
- **AND** 模型列表来自 `GET /opencode/models`

#### Scenario: 搜索过滤模型

- **WHEN** 用户在搜索框中输入 "claude"
- **THEN** 下拉列表只显示包含 "claude" 的模型

#### Scenario: 选择模型并保存

- **WHEN** 用户从下拉列表选择 "anthropic/claude-sonnet-4"
- **THEN** 调用 `PUT /api/opencode-config/model` 传入 `{ model: "anthropic/claude-sonnet-4" }`
- **AND** 成功后按钮显示更新为 "claude-sonnet-4"

#### Scenario: 选择失败显示错误

- **WHEN** `PUT /api/opencode-config/model` 返回错误
- **THEN** 显示错误提示信息
- **AND** 按钮恢复为修改前的值

### Requirement: Default Coder Model 支持清除

用户 SHALL 能清除已配置的 default coder model，恢复为 opencode 内部默认行为。

#### Scenario: 清除已配置的模型

- **WHEN** 当前已配置模型（如 "anthropic/claude-sonnet-4"）
- **AND** 用户点击清除操作
- **THEN** 调用 `PUT /api/opencode-config/model` 传入 `{ model: null }`
- **AND** 成功后按钮显示 placeholder "opencode default"

#### Scenario: 无配置时无清除操作

- **WHEN** 当前 default coder model 为 null
- **THEN** 不显示清除操作

### Requirement: Default Coder Model 字段布局

"Default Coder Model" 字段 SHALL 位于 GeneralSettingsSection 中现有数值配置字段（Agent Timeout、Max Concurrent Agents、Poll Interval）之后、分隔线之前。

#### Scenario: 字段位置

- **WHEN** 用户查看 Settings > General
- **THEN** 字段排列顺序为：Agent Timeout → Max Concurrent Agents → Poll Interval → Default Coder Model → 分隔线 → Reset to Defaults
