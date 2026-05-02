## MODIFIED Requirements

### Requirement: AI Settings 页面布局结构

AI Settings 页面 SHALL 按以下顺序排列各区域：Model Selection → Providers → Custom Providers → Stage Model Overrides。Provider 列表 SHALL 将已连接的 provider 和未连接的 provider 分为两个视觉分组，未连接 provider 分组 SHALL 默认折叠。

#### Scenario: Model Selection 位于页面顶部

- **WHEN** 用户打开 Settings AI 页面
- **THEN** Model Selection 区域（包含 Mohist Model 和 Coder Model 选择器）在 Providers 列表之前显示

#### Scenario: 已连接 Provider 分组

- **WHEN** 存在已配置的内置 provider
- **THEN** 这些 provider 在 "Connected" 分组中显示，带有连接状态标记和 Remove 操作

#### Scenario: 未连接 Provider 分组默认折叠

- **WHEN** 存在未配置的内置 provider
- **THEN** 这些 provider 显示在可折叠的 "Available Providers" 分组中
- **AND** 该分组默认折叠，仅显示分组标题和 provider 数量
- **AND** 用户点击标题可展开查看完整列表

#### Scenario: Provider 搜索保留

- **WHEN** 用户在 Provider 搜索框输入文本
- **THEN** 按名称或 ID 过滤 provider 列表（跨分组）
