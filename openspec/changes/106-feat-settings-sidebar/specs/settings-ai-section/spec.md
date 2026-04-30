## ADDED Requirements

### Requirement: Provider 统一列表

AI section SHALL 显示一个统一的 Provider 列表，将已连接（●）和未连接（○）的 provider 合并在同一列表中。已连接的 provider 排在前面，未连接的排在后面。列表顶部 SHALL 显示搜索框。

#### Scenario: 已连接 provider 排在前面
- **WHEN** 系统有 2 个已连接 provider（Anthropic、OpenAI）和 5 个未连接 provider
- **THEN** 列表前 2 项为已连接的 Anthropic 和 OpenAI
- **AND** 后 5 项为未连接的 provider
- **AND** 已连接 provider 显示实心圆点（●）和 [Remove] 按钮
- **AND** 未连接 provider 显示空心圆点（○）和 [Connect] 按钮

#### Scenario: 已连接 provider 显示详情
- **WHEN** provider 已配置（apiKey 存在于 config 或 env）
- **THEN** 显示 provider 名称 + masked API key + 来源标签（config/env）
- **AND** 显示 [Remove] 按钮

#### Scenario: 未连接 provider 显示描述
- **WHEN** provider 未配置
- **THEN** 显示 provider 名称 + 简短描述
- **AND** 显示 [Connect] 按钮

#### Scenario: 搜索过滤 provider
- **WHEN** 用户在搜索框输入 "deep"
- **THEN** 列表只显示名称或 id 包含 "deep" 的 provider（如 DeepSeek）
- **AND** 已连接匹配项仍排在未连接匹配项前面

#### Scenario: 无匹配搜索结果
- **WHEN** 用户搜索不匹配任何 provider 的关键词
- **THEN** 显示空状态提示 "No providers match your search"

#### Scenario: 列表顶部添加按钮
- **WHEN** 列表标题区域包含 [+ 添加] 按钮
- **THEN** 点击后打开 CustomProviderDialog

### Requirement: Custom Providers 独立子区域

统一列表下方 SHALL 有独立的 Custom Providers 子区域，展示用户自定义的 provider。Custom Providers 区域有自己的 [+ 添加] 按钮。

#### Scenario: 无 custom provider 时显示空状态
- **WHEN** 没有自定义 provider
- **THEN** Custom Providers 区域显示简短说明文字 "Configure a custom OpenAI-compatible provider"
- **AND** 显示 [+ 添加] 按钮

#### Scenario: 有 custom provider 时显示列表
- **WHEN** 用户配置了自定义 provider
- **THEN** 显示自定义 provider 列表，每项包含名称、baseURL、[Remove] 按钮

### Requirement: Mohist Model 选择器

AI section SHALL 提供 Mohist Model 下拉选择器，对应 `config.model`。该模型用于 explore/plan stages。选择器 SHALL 列出所有已连接 provider 支持的模型。

#### Scenario: 显示当前模型
- **WHEN** `config.model` 为 "anthropic/claude-sonnet-4-20250514"
- **THEN** Mohist Model 选择器显示 "anthropic/claude-sonnet-4-20250514"

#### Scenario: 切换模型
- **WHEN** 用户从下拉中选择 "openai/gpt-4o"
- **THEN** 调用 API 更新 `config.model` 为 "openai/gpt-4o"
- **AND** 成功后选择器显示新值

#### Scenario: 无模型配置时显示默认值
- **WHEN** `config.model` 未配置
- **THEN** Mohist Model 选择器显示系统默认值 "anthropic/claude-sonnet-4-20250514"

#### Scenario: 选择器列出可用模型
- **WHEN** 用户展开 Mohist Model 下拉
- **THEN** 列出所有已连接 provider 支持的模型列表（从 `GET /api/opencode/models` 或内置模型列表获取）
- **AND** 按模型 ID 排序

### Requirement: Coder Model 选择器

AI section SHALL 提供 Coder Model 下拉选择器，对应 `config.opencode.model`。该模型用于 build/review/fix stages。选择器 SHALL 支持清空（Clear），清空后使用 Mohist Model 作为 fallback。

#### Scenario: 显示当前 coder 模型
- **WHEN** `config.opencode.model` 为 "deepseek/deepseek-chat"
- **THEN** Coder Model 选择器显示 "deepseek/deepseek-chat"

#### Scenario: 清空 coder 模型
- **WHEN** 用户点击 Coder Model 旁边的 [Clear] 按钮
- **THEN** 调用 API 清除 `config.opencode.model`
- **AND** 选择器显示为空，表示使用 Mohist Model fallback

#### Scenario: 未配置 coder 模型时显示 fallback 提示
- **WHEN** `config.opencode.model` 未配置
- **THEN** 选择器显示占位文本 "Same as Mohist Model"

### Requirement: Stage Model Overrides 折叠面板

AI section SHALL 在 Model Selection 下方提供 "Stage Model Overrides" 折叠面板，默认折叠。展开后显示各 stage（explore、plan、build、review、fix）的独立模型选择器，对应 `config.opencode.stageModels`。

#### Scenario: 默认折叠
- **WHEN** AI section 首次加载
- **THEN** "Stage Model Overrides" 面板折叠
- **AND** 显示 ▸ 图标和 "高级" 标签

#### Scenario: 展开显示 stage 模型选择器
- **WHEN** 用户点击 "Stage Model Overrides" 标题
- **THEN** 面板展开
- **AND** 显示每个 stage 的独立模型下拉选择器
- **AND** 未覆盖的 stage 显示 "Default" 占位文本

#### Scenario: 设置 stage model override
- **WHEN** 用户为 "build" stage 选择 "openai/gpt-4o"
- **THEN** 调用 API 更新 `config.opencode.stageModels.build` 为 "openai/gpt-4o"
- **AND** build stage 选择器显示新值
