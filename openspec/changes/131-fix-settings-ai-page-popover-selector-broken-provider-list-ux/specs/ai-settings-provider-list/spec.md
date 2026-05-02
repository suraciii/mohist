## ADDED Requirements

### Requirement: Provider 列表按连接状态分组显示

AI Settings 页面的 Provider 列表 SHALL 将 providers 按连接状态分为两组：「Connected」和「Available」，每组有独立的标题和视觉分隔。

#### Scenario: 有已连接和未连接 providers

- **WHEN** 用户访问 Settings AI 页面
- **AND** 存在 1 个已连接 provider 和多个未连接 providers
- **THEN** 列表显示「Connected」分组（含已连接 providers）
- **AND** 显示「Available」分组（含未连接 providers）
- **AND** 两组之间有明确的视觉分隔

#### Scenario: 全部未连接

- **WHEN** 用户访问 Settings AI 页面
- **AND** 没有已连接 providers
- **THEN** 不显示「Connected」分组
- **AND** 仅显示「Available」分组

#### Scenario: 全部已连接

- **WHEN** 所有 builtin providers 均已配置
- **THEN** 不显示「Available」分组
- **AND** 仅显示「Connected」分组

### Requirement: Available providers 默认折叠

「Available」分组 SHALL 默认折叠，显示 provider 数量摘要，用户点击后展开完整列表。

#### Scenario: Available 分组默认折叠

- **WHEN** 用户访问 Settings AI 页面
- **AND** 存在未连接 providers
- **THEN** 「Available」分组显示为折叠状态
- **AND** 显示摘要文字（如 "12 available providers"）

#### Scenario: 展开 Available 分组

- **WHEN** 用户点击折叠的「Available」分组标题
- **THEN** 分组展开，显示所有未连接 provider 卡片
- **AND** 再次点击可重新折叠

### Requirement: Model Selection 区域置于 Provider 列表之上

AI Settings 页面的「Model Selection」区域 SHALL 排列在 Provider 列表之前，确保用户无需滚动即可选择模型。

#### Scenario: 页面布局顺序

- **WHEN** 用户访问 Settings AI 页面
- **THEN** 页面自上而下的顺序为：Model Selection → Connected Providers → Available Providers（折叠）→ Custom Providers → Stage Model Overrides

#### Scenario: Model Selection 可见性

- **WHEN** Settings AI 页面在标准视口（1080p）下加载完成
- **THEN** Model Selection 区域（含 Mohist Model 和 Coder Model 选择器）无需滚动即可完全可见
