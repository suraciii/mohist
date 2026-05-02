## MODIFIED Requirements

### Requirement: AI Settings 页面布局顺序

AI Settings 页面 SHALL 按以下顺序排列各区域，将最常用的 Model Selection 置于页面顶部：

1. **Model Selection** — Mohist Model + Coder Model + Stage Model Overrides
2. **Connected Providers** — 已配置的内置 provider 列表（展开状态）
3. **Available Providers** — 未配置的内置 provider 列表（默认折叠）
4. **Custom Providers** — 自定义 provider 列表

#### Scenario: Model Selection 区域在页面顶部

- **WHEN** 用户打开 Settings AI 页面
- **THEN** Model Selection 区域（包含 Mohist Model 和 Coder Model 选择器）为第一个可见区域
- **AND** Provider 列表区域在 Model Selection 下方

#### Scenario: Connected Providers 默认展开

- **WHEN** 用户打开 Settings AI 页面
- **AND** 存在已配置的 provider
- **THEN** Connected Providers 区域默认展开，显示所有已连接的 provider 卡片

#### Scenario: Available Providers 默认折叠

- **WHEN** 用户打开 Settings AI 页面
- **AND** 存在未配置的 provider
- **THEN** Available Providers 区域默认折叠，显示标题和 provider 数量（如 "Available (78)"）
- **AND** 用户点击标题可展开查看完整列表

#### Scenario: 无已配置 provider 时 Model Selection 仍可访问

- **WHEN** 用户打开 Settings AI 页面
- **AND** 没有任何已配置的 provider
- **THEN** Model Selection 区域仍然显示在选择器位置
- **AND** Connected Providers 区域显示空状态或不显示
